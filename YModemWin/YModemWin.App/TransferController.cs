using System.IO.Ports;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DeviceProgramming.FileFormat;
using DeviceProgramming.Memory;
using Serilog.Context;
using YModemWin.Core;

namespace YModemWin;

public sealed class TransferController : IDisposable
{
    private readonly object serialLock = new();
    private SerialPort? activePort;
    private YModemTransmitter? transmitter;
    private YModemReceiver? receiver;
    private bool sendCancellationRequested;
    private bool receiveCancellationRequested;
    private DateTime sendStartedAtUtc;
    private DateTime receiveStartedAtUtc;
    private long lastSentBytes;
    private long lastReceivedBytes;
    private long lastSendStatus;
    private long lastReceiveStatus;
    private string lastSendMessage = string.Empty;
    private string lastReceiveMessage = string.Empty;
    private bool sendOutcomeMetricReported;
    private bool receiveOutcomeMetricReported;
    private static readonly Regex SendingFileMessageRegex = new(@"^Sending file (?<file>.+)$", RegexOptions.Compiled);
    private static readonly Regex ReceivingFileMessageRegex = new(@"^Receiving file (?<file>.+)$", RegexOptions.Compiled);
    private static readonly Regex SendCompletedMessageRegex = new(
        @"^Send completed\. wait: (?<wait>[\d.,]+)s, transfer: (?<transfer>[\d.,]+)s, total: (?<total>[\d.,]+)s$",
        RegexOptions.Compiled);
    private static readonly Regex ReceiveCompletedMessageRegex = new(
        @"^Receive completed\. wait: (?<wait>[\d.,]+)s, transfer: (?<transfer>[\d.,]+)s, total: (?<total>[\d.,]+)s$",
        RegexOptions.Compiled);
    private static readonly Regex MaxRetryExceededMessageRegex = new(
        @"^Max retry count \((?<count>\d+)\) exceeded\. Transfer aborted\.$",
        RegexOptions.Compiled);
    private static readonly Regex UnsupportedProtocolActionMessageRegex = new(
        @"^Unsupported protocol action: (?<action>.+)$",
        RegexOptions.Compiled);

    public bool IsSending { get; private set; }

    public bool IsReceiving { get; private set; }

    public event Action<SendProgressSnapshot>? SendProgressChanged;

    public event Action<ReceiveProgressSnapshot>? ReceiveProgressChanged;

    public string[] GetAvailablePorts() => SerialPort.GetPortNames().OrderBy(name => name).ToArray();

    public PreparedSendFile PrepareSendFile(string sourcePath, bool parsePreferred)
    {
        if (!parsePreferred)
        {
            return PreparedSendFile.FromRawFile(sourcePath);
        }

        var extension = Path.GetExtension(sourcePath);
        var parserName = GetFirmwareParserName(extension);
        if (parserName is null)
        {
            return PreparedSendFile.FromRawFile(sourcePath);
        }

        var memory = ParseFirmwareMemory(sourcePath, extension);
        var segments = memory.Segments
            .Where(segment => segment.Data is { Length: > 0 })
            .OrderBy(segment => segment.StartAddress)
            .ToList();
        if (segments.Count == 0)
        {
            throw new InvalidDataException($"No segments found in {Path.GetFileName(sourcePath)}");
        }

        var payload = segments[0].Data.ToArray();
        if (payload.Length == 0)
        {
            throw new InvalidDataException($"No payload bytes found in {Path.GetFileName(sourcePath)}");
        }

        AppLogger.Info("Prepared parsed payload for {FileName} via {Parser}. SegmentCount={SegmentCount}, PayloadSize={PayloadSize}",
            Path.GetFileName(sourcePath), parserName, segments.Count, payload.Length);

        return PreparedSendFile.FromParsedData(sourcePath, payload, parserName, segments.Count);
    }

    public async Task StartSendAsync(
        string portName,
        int baudRate,
        int timeoutSeconds,
        IReadOnlyList<PreparedSendFile> files,
        string dataBlockMode = "Dynamic1K",
        bool use1KBlock0 = true,
        bool use1KFinalDataBlock = true)
    {
        if (files.Count == 0)
        {
            throw new InvalidOperationException(GetLocalizedText("NoFilesSelected", "No files selected."));
        }

        if (IsReceiving || IsSending)
        {
            throw new InvalidOperationException(GetLocalizedText("TransferAlreadyInProgress", "Transfer is already in progress."));
        }

        var transferId = CreateTransferId();
        var transferStopwatch = Stopwatch.StartNew();
        var transferResult = "unknown";
        string? transferErrorCode = null;
        string? transferErrorMessage = null;
        var sendFinishedSuccessfully = false;

        var serialPort = OpenPort(portName, baudRate);
        sendCancellationRequested = false;
        sendStartedAtUtc = DateTime.UtcNow;
        lastSentBytes = 0;
        lastSendStatus = 0;
        lastSendMessage = string.Empty;
        sendOutcomeMetricReported = false;
        IsSending = true;
        var totalSendBytes = files.Sum(GetSendFilePayloadLength);
        var fixed1KOverridesEnabled = string.Equals(dataBlockMode, "Fixed1K", StringComparison.Ordinal);
        bool? block0Config = fixed1KOverridesEnabled ? use1KBlock0 : null;
        bool? finalBlockConfig = fixed1KOverridesEnabled ? use1KFinalDataBlock : null;
        AppMetrics.EmitTransferStart("send", files.Count, totalSendBytes);
        using var transferScope = PushTransferLogContext(transferId, "Send", portName, baudRate, timeoutSeconds);
        AppLogger.RegisterTransferStarted(
            transferId,
            "send",
            portName,
            baudRate,
            timeoutSeconds,
            files.Count,
            totalSendBytes,
            dataBlockMode,
            block0Config,
            finalBlockConfig,
            saveFolder: null);
        AppLogger.RecordTransferConfigSnapshot(
            transferId,
            "send",
            [
                new KeyValuePair<string, object?>("port_name", portName),
                new KeyValuePair<string, object?>("baud_rate", baudRate),
                new KeyValuePair<string, object?>("timeout_seconds", timeoutSeconds),
                new KeyValuePair<string, object?>("data_block_mode", dataBlockMode),
                new KeyValuePair<string, object?>("data_block_mode_description", DescribeDataBlockMode(dataBlockMode)),
                new KeyValuePair<string, object?>("use_1k_block0", block0Config),
                new KeyValuePair<string, object?>("use_1k_final_data_block", finalBlockConfig),
                new KeyValuePair<string, object?>("file_count", files.Count),
                new KeyValuePair<string, object?>("total_bytes", totalSendBytes)
            ]);
        if (fixed1KOverridesEnabled)
        {
            AppLogger.Debug(
                "Send session config: transferId={TransferId}, port={PortName}, baud={BaudRate}, timeoutSec={TimeoutSeconds}, dataBlock={DataBlockModeDescription}, use1KBlock0={Use1KBlock0}, use1KFinalDataBlock={Use1KFinalDataBlock}, files={FileCount}, totalBytes={TotalBytes}",
                transferId,
                portName,
                baudRate,
                timeoutSeconds,
                DescribeDataBlockMode(dataBlockMode),
                use1KBlock0,
                use1KFinalDataBlock,
                files.Count,
                totalSendBytes);
        }
        else
        {
            AppLogger.Debug(
                "Send session config: transferId={TransferId}, port={PortName}, baud={BaudRate}, timeoutSec={TimeoutSeconds}, dataBlock={DataBlockModeDescription}, block0=auto, finalDataBlock=auto, files={FileCount}, totalBytes={TotalBytes}",
                transferId,
                portName,
                baudRate,
                timeoutSeconds,
                DescribeDataBlockMode(dataBlockMode),
                files.Count,
                totalSendBytes);
        }

        SendProgressChanged?.Invoke(new SendProgressSnapshot(
            SentBytes: 0,
            TotalBytes: 0,
            SentPackets: 0,
            TotalPackets: 0,
            Status: 0,
            Message: GetLocalizedText("WaitingForReceiverHandshake", "Waiting for receiver handshake...")));

        transmitter = new YModemTransmitter(serialPort, timeoutSeconds, OnSendProgress);
        transmitter.ConfigureBatchDataBlockOptions(dataBlockMode, use1KBlock0, use1KFinalDataBlock);

        try
        {
            await Task.Run(() =>
            {
                var allFilesSent = true;
                for (var i = 0; i < files.Count; i++)
                {
                    var item = files[i];
                    var isLastFile = i == files.Count - 1;
                    using var contextScope = PushSendFileLogContext(item);
                    var expectedSize = GetSendFilePayloadLength(item);
                    var checksum = ComputePreparedFileSha256(item);
                    var sent = item.ParsedPayload is null
                        ? transmitter.YmodemSendFile(item.SourcePath, isLastFile)
                        : transmitter.YmodemSendParsedData(item.DisplayFileName, item.LastWriteTime, item.ParsedPayload, isLastFile);
                    AppLogger.RecordFileValidation(
                        transferId,
                        i,
                        "send",
                        item.DisplayFileName,
                        item.SourcePath,
                        item.IsParsedPayload ? "parsed_payload" : "raw_file",
                        item.IsParsedPayload,
                        expectedSize,
                        sent ? expectedSize : null,
                        "SHA256",
                        checksum,
                        sent ? true : null,
                        null,
                        sent ? "Source payload checksum captured before sending." : "Send not completed for this file.");

                    if (!sent)
                    {
                        allFilesSent = false;
                        break;
                    }
                }

                sendFinishedSuccessfully = allFilesSent;
            });
        }
        catch (Exception ex)
        {
            transferResult = "failure";
            transferErrorCode = "exception";
            transferErrorMessage = ex.Message;
            throw;
        }
        finally
        {
            if (!sendOutcomeMetricReported)
            {
                EmitSendOutcomeMetric(sendCancellationRequested ? "cancelled" : "unknown", lastSentBytes);
            }

            if (sendCancellationRequested)
            {
                transferResult = "cancelled";
                transferErrorCode ??= "user_cancelled";
                transferErrorMessage ??= "Send canceled by user.";
            }
            else if (lastSendStatus == 1 && sendFinishedSuccessfully)
            {
                transferResult = "success";
            }
            else if (transferResult != "failure")
            {
                transferResult = "failure";
                transferErrorCode ??= DetermineErrorCode(lastSendMessage, "send_failed");
                transferErrorMessage ??= string.IsNullOrWhiteSpace(lastSendMessage) ? "Send failed." : lastSendMessage;
            }

            AppLogger.RegisterTransferCompleted(
                transferId,
                transferResult,
                lastSentBytes,
                transferStopwatch.Elapsed.TotalMilliseconds,
                transmitter?.TotalRetryCount ?? 0,
                transferErrorCode,
                transferErrorMessage);

            IsSending = false;
            ClosePort();
            transmitter = null;
        }
    }

    public async Task StartReceiveAsync(string portName, int baudRate, int timeoutSeconds, string saveFolder)
    {
        if (!Directory.Exists(saveFolder))
        {
            throw new InvalidOperationException(GetLocalizedText("SaveFolderNotExist", "Save folder does not exist."));
        }

        if (IsSending || IsReceiving)
        {
            throw new InvalidOperationException(GetLocalizedText("TransferAlreadyInProgress", "Transfer is already in progress."));
        }

        var transferId = CreateTransferId();
        var transferStopwatch = Stopwatch.StartNew();
        var transferResult = "unknown";
        string? transferErrorCode = null;
        string? transferErrorMessage = null;

        var serialPort = OpenPort(portName, baudRate);
        receiveCancellationRequested = false;
        receiveStartedAtUtc = DateTime.UtcNow;
        lastReceivedBytes = 0;
        lastReceiveStatus = 0;
        lastReceiveMessage = string.Empty;
        receiveOutcomeMetricReported = false;
        IsReceiving = true;
        using var transferScope = PushTransferLogContext(transferId, "Receive", portName, baudRate, timeoutSeconds);
        AppLogger.RegisterTransferStarted(
            transferId,
            "receive",
            portName,
            baudRate,
            timeoutSeconds,
            fileCount: 0,
            totalBytes: 0,
            dataBlockMode: null,
            use1KBlock0: null,
            use1KFinalDataBlock: null,
            saveFolder: saveFolder);
        AppLogger.RecordTransferConfigSnapshot(
            transferId,
            "receive",
            [
                new KeyValuePair<string, object?>("port_name", portName),
                new KeyValuePair<string, object?>("baud_rate", baudRate),
                new KeyValuePair<string, object?>("timeout_seconds", timeoutSeconds),
                new KeyValuePair<string, object?>("save_folder", saveFolder)
            ]);
        AppMetrics.EmitTransferStart("receive", 0, 0);

        ReceiveProgressChanged?.Invoke(new ReceiveProgressSnapshot(
            ReceivedBytes: 0,
            TotalBytes: 0,
            PacketNo: 0,
            TotalPacket: 0,
            Status: 0,
            Message: GetLocalizedText("WaitingForSenderHandshake", "Waiting for sender handshake..."),
            FileName: string.Empty,
            FileDate: string.Empty));

        receiver = new YModemReceiver(serialPort, timeoutSeconds, saveFolder, OnReceiveProgress);

        try
        {
            await Task.Run(() => receiver.StartReceiving());
        }
        catch (Exception ex)
        {
            transferResult = "failure";
            transferErrorCode = "exception";
            transferErrorMessage = ex.Message;
            throw;
        }
        finally
        {
            if (!receiveOutcomeMetricReported)
            {
                EmitReceiveOutcomeMetric(receiveCancellationRequested ? "cancelled" : "unknown", lastReceivedBytes);
            }

            if (lastReceiveStatus == 1
                && receiver is not null)
            {
                var receivedFiles = receiver.CompletedFiles;
                if (receivedFiles.Count == 0
                    && receiver.saveFilePath is { } fallbackPath
                    && File.Exists(fallbackPath))
                {
                    var fallbackActual = new FileInfo(fallbackPath).Length;
                    var fallbackExpected = receiver.fileLength > 0 ? receiver.fileLength : fallbackActual;
                    AppLogger.RecordFileValidation(
                        transferId,
                        0,
                        "receive",
                        receiver.saveFileName ?? Path.GetFileName(fallbackPath),
                        fallbackPath,
                        "received_file",
                        isParsedPayload: false,
                        fallbackExpected,
                        fallbackActual,
                        "SHA256",
                        ComputeFileSha256(fallbackPath),
                        fallbackExpected == fallbackActual,
                        null,
                        "Checksum captured after receive completion.");
                }
                else
                {
                    for (var i = 0; i < receivedFiles.Count; i++)
                    {
                        var receivedFile = receivedFiles[i];
                        AppLogger.RecordFileValidation(
                            transferId,
                            i,
                            "receive",
                            receivedFile.FileName,
                            receivedFile.FilePath,
                            "received_file",
                            isParsedPayload: false,
                            receivedFile.ExpectedSize,
                            receivedFile.ActualSize,
                            "SHA256",
                            ComputeFileSha256(receivedFile.FilePath),
                            receivedFile.ExpectedSize == receivedFile.ActualSize,
                            null,
                            "Checksum captured after receive completion.");
                    }
                }
            }

            if (receiveCancellationRequested)
            {
                transferResult = "cancelled";
                transferErrorCode ??= "user_cancelled";
                transferErrorMessage ??= "Receive canceled by user.";
            }
            else if (lastReceiveStatus == 1)
            {
                transferResult = "success";
            }
            else if (transferResult != "failure")
            {
                transferResult = "failure";
                transferErrorCode ??= DetermineErrorCode(lastReceiveMessage, "receive_failed");
                transferErrorMessage ??= string.IsNullOrWhiteSpace(lastReceiveMessage) ? "Receive failed." : lastReceiveMessage;
            }

            AppLogger.RegisterTransferCompleted(
                transferId,
                transferResult,
                lastReceivedBytes,
                transferStopwatch.Elapsed.TotalMilliseconds,
                receiver?.RetryCount ?? 0,
                transferErrorCode,
                transferErrorMessage);

            IsReceiving = false;
            ClosePort();
            receiver = null;
        }
    }

    public void CancelSend()
    {
        if (!IsSending)
        {
            return;
        }

        sendCancellationRequested = true;
        transmitter?.StopTransmitting();
        lastSendStatus = -1;
        lastSendMessage = "Send canceled by user.";
        EmitSendOutcomeMetric("cancelled", lastSentBytes);

        SendProgressChanged?.Invoke(new SendProgressSnapshot(
            SentBytes: 0,
            TotalBytes: 0,
            SentPackets: 0,
            TotalPackets: 0,
            Status: -1,
            Message: GetLocalizedText("SendCanceledByUser", "Send canceled by user.")));
    }

    public void CancelReceive()
    {
        if (!IsReceiving)
        {
            return;
        }

        receiveCancellationRequested = true;
        receiver?.StopReceiving();
        lastReceiveStatus = -1;
        lastReceiveMessage = "Receive canceled by user.";
        EmitReceiveOutcomeMetric("cancelled", lastReceivedBytes);

        ReceiveProgressChanged?.Invoke(new ReceiveProgressSnapshot(
            ReceivedBytes: 0,
            TotalBytes: 0,
            PacketNo: 0,
            TotalPacket: 0,
            Status: -1,
            Message: GetLocalizedText("ReceiveCanceledByUser", "Receive canceled by user."),
            FileName: string.Empty,
            FileDate: string.Empty));
    }

    private static RawMemory ParseFirmwareMemory(string filePath, string extension)
    {
        return string.Equals(extension, ".hex", StringComparison.OrdinalIgnoreCase) ? IntelHex.ParseFile(filePath) : SRecord.ParseFile(filePath);
    }

    private static string? GetFirmwareParserName(string extension)
    {
        if (string.Equals(extension, ".hex", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(IntelHex);
        }

        if (string.Equals(extension, ".s19", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".s37", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".srec", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(SRecord);
        }

        return null;
    }

    private static long GetSendFilePayloadLength(PreparedSendFile file)
    {
        if (file.ParsedPayload is not null)
        {
            return file.ParsedPayload.LongLength;
        }

        return new FileInfo(file.SourcePath).Length;
    }

    private static string DescribeDataBlockMode(string dataBlockMode)
    {
        return dataBlockMode switch
        {
            "Fixed128" => GetLocalizedText("DataBlockModeDescriptionFixed128", "Fixed128(128B)"),
            "Fixed1K" => GetLocalizedText("DataBlockModeDescriptionFixed1K", "Fixed1K(1024B)"),
            "Dynamic1K" => GetLocalizedText("DataBlockModeDescriptionDynamic1K", "Dynamic1K(<=128B:128B,>128B:1024B)"),
            _ => dataBlockMode
        };
    }

    private static string GetLocalizedText(string key, string fallback)
    {
        var value = Properties.Resources.ResourceManager.GetString(key, Properties.Resources.Culture);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private void OnSendProgress(long sentBytes, long totalBytes, long sentPackets, long totalPackets, long status, string message)
    {
        lastSentBytes = sentBytes;
        lastSendStatus = status;
        lastSendMessage = message;
        if (status == 1)
        {
            EmitSendOutcomeMetric("success", sentBytes);
        }
        else if (status < 0)
        {
            EmitSendOutcomeMetric("failure", sentBytes);
        }

        if (sendCancellationRequested)
        {
            return;
        }

        SendProgressChanged?.Invoke(new SendProgressSnapshot(
            sentBytes,
            totalBytes,
            sentPackets,
            totalPackets,
            status,
            LocalizeSendProgressMessage(message)));
    }

    private void OnReceiveProgress(long receivedBytes, long totalBytes, long packetNo, long totalPacket, long status, string message, string fileName, string fileDate)
    {
        lastReceivedBytes = receivedBytes;
        lastReceiveStatus = status;
        lastReceiveMessage = message;
        if (status == 1)
        {
            EmitReceiveOutcomeMetric("success", receivedBytes);
        }
        else if (status < 0)
        {
            EmitReceiveOutcomeMetric("failure", receivedBytes);
        }

        if (receiveCancellationRequested)
        {
            return;
        }

        ReceiveProgressChanged?.Invoke(new ReceiveProgressSnapshot(
            receivedBytes,
            totalBytes,
            packetNo,
            totalPacket,
            status,
            LocalizeReceiveProgressMessage(message),
            fileName,
            fileDate));
    }

    private static string LocalizeSendProgressMessage(string message)
    {
        var localized = LocalizeCommonProgressMessage(message);
        if (string.IsNullOrWhiteSpace(localized))
        {
            return localized;
        }

        var sendingMatch = SendingFileMessageRegex.Match(localized);
        if (sendingMatch.Success)
        {
            return string.Format(
                CultureInfo.CurrentUICulture,
                GetLocalizedText("SendProgressFileFormat", "Sending file {0}"),
                sendingMatch.Groups["file"].Value);
        }

        var completedMatch = SendCompletedMessageRegex.Match(localized);
        if (completedMatch.Success)
        {
            return string.Format(
                CultureInfo.CurrentUICulture,
                GetLocalizedText("SendCompletedWithTimingFormat", "Send completed. wait: {0}s, transfer: {1}s, total: {2}s"),
                completedMatch.Groups["wait"].Value,
                completedMatch.Groups["transfer"].Value,
                completedMatch.Groups["total"].Value);
        }

        return localized;
    }

    private static string LocalizeReceiveProgressMessage(string message)
    {
        var localized = LocalizeCommonProgressMessage(message);
        if (string.IsNullOrWhiteSpace(localized))
        {
            return localized;
        }

        var receivingMatch = ReceivingFileMessageRegex.Match(localized);
        if (receivingMatch.Success)
        {
            return string.Format(
                CultureInfo.CurrentUICulture,
                GetLocalizedText("ReceiveProgressFileFormat", "Receiving file {0}"),
                receivingMatch.Groups["file"].Value);
        }

        var completedMatch = ReceiveCompletedMessageRegex.Match(localized);
        if (completedMatch.Success)
        {
            return string.Format(
                CultureInfo.CurrentUICulture,
                GetLocalizedText("ReceiveCompletedWithTimingFormat", "Receive completed. wait: {0}s, transfer: {1}s, total: {2}s"),
                completedMatch.Groups["wait"].Value,
                completedMatch.Groups["transfer"].Value,
                completedMatch.Groups["total"].Value);
        }

        return localized;
    }

    private static string LocalizeCommonProgressMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        if (string.Equals(message, "Send canceled by user.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(message, "Send canceled by user", StringComparison.OrdinalIgnoreCase))
        {
            return GetLocalizedText("SendCanceledByUser", "Send canceled by user.");
        }

        if (string.Equals(message, "Receive canceled by user.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(message, "Receive canceled by user", StringComparison.OrdinalIgnoreCase))
        {
            return GetLocalizedText("ReceiveCanceledByUser", "Receive canceled by user.");
        }

        if (string.Equals(message, "Receiver timeout", StringComparison.OrdinalIgnoreCase))
        {
            return GetLocalizedText("ReceiverTimeout", "Receiver timeout");
        }

        if (string.Equals(message, "Data receive timeout", StringComparison.OrdinalIgnoreCase))
        {
            return GetLocalizedText("DataReceiveTimeout", "Data receive timeout");
        }

        if (string.Equals(message, "Unexpected file header request from protocol state machine.", StringComparison.OrdinalIgnoreCase))
        {
            return GetLocalizedText(
                "UnexpectedFileHeaderRequest",
                "Unexpected file header request from protocol state machine.");
        }

        if (string.Equals(message, "Unsupported receive action", StringComparison.OrdinalIgnoreCase))
        {
            return GetLocalizedText("UnsupportedReceiveAction", "Unsupported receive action");
        }

        var maxRetryMatch = MaxRetryExceededMessageRegex.Match(message);
        if (maxRetryMatch.Success)
        {
            return string.Format(
                CultureInfo.CurrentUICulture,
                GetLocalizedText("MaxRetryExceededFormat", "Max retry count ({0}) exceeded. Transfer aborted."),
                maxRetryMatch.Groups["count"].Value);
        }

        var unsupportedProtocolActionMatch = UnsupportedProtocolActionMessageRegex.Match(message);
        if (unsupportedProtocolActionMatch.Success)
        {
            return string.Format(
                CultureInfo.CurrentUICulture,
                GetLocalizedText("UnsupportedProtocolActionFormat", "Unsupported protocol action: {0}"),
                unsupportedProtocolActionMatch.Groups["action"].Value);
        }

        return message;
    }

    private SerialPort OpenPort(string portName, int baudRate)
    {
        lock (serialLock)
        {
            if (activePort?.IsOpen == true)
            {
                return activePort;
            }

            var serialPort = new SerialPort(portName, baudRate)
            {
                Parity = Parity.None,
                DataBits = 8,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false
            };

            serialPort.Open();
            activePort = serialPort;
            return serialPort;
        }
    }

    private void ClosePort()
    {
        lock (serialLock)
        {
            if (activePort is null)
            {
                return;
            }

            try
            {
                if (activePort.IsOpen)
                {
                    activePort.Close();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to close serial port");
            }
            finally
            {
                activePort.Dispose();
                activePort = null;
            }
        }
    }

    private void EmitSendOutcomeMetric(string outcome, long bytes)
    {
        if (sendOutcomeMetricReported)
        {
            return;
        }

        sendOutcomeMetricReported = true;
        var elapsedMs = sendStartedAtUtc == default ? 0 : Math.Max(0, (DateTime.UtcNow - sendStartedAtUtc).TotalMilliseconds);
        AppMetrics.EmitTransferOutcome("send", outcome, bytes, elapsedMs);
    }

    private void EmitReceiveOutcomeMetric(string outcome, long bytes)
    {
        if (receiveOutcomeMetricReported)
        {
            return;
        }

        receiveOutcomeMetricReported = true;
        var elapsedMs = receiveStartedAtUtc == default ? 0 : Math.Max(0, (DateTime.UtcNow - receiveStartedAtUtc).TotalMilliseconds);
        AppMetrics.EmitTransferOutcome("receive", outcome, bytes, elapsedMs);
    }

    private static string CreateTransferId() => Guid.NewGuid().ToString("N");

    private static IDisposable PushTransferLogContext(string transferId, string direction, string portName, int baudRate, int timeoutSeconds)
    {
        var scopes = new List<IDisposable>
        {
            LogContext.PushProperty("TransferId", transferId),
            LogContext.PushProperty("TransferDirection", direction),
            LogContext.PushProperty("PortName", portName),
            LogContext.PushProperty("BaudRate", baudRate),
            LogContext.PushProperty("TimeoutSeconds", timeoutSeconds)
        };
        return new CompositeScope(scopes);
    }

    private static string ComputePreparedFileSha256(PreparedSendFile file)
    {
        if (file.ParsedPayload is not null)
        {
            return Convert.ToHexString(SHA256.HashData(file.ParsedPayload));
        }

        return ComputeFileSha256(file.SourcePath);
    }

    private static string ComputeFileSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string DetermineErrorCode(string? message, string defaultCode)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return defaultCode;
        }

        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "timeout";
        }

        if (message.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
            return "cancelled";
        }

        if (message.Contains("retry", StringComparison.OrdinalIgnoreCase))
        {
            return "max_retry_exceeded";
        }

        return defaultCode;
    }

    public void Dispose()
    {
        CancelSend();
        CancelReceive();
        ClosePort();
    }

    private static IDisposable PushSendFileLogContext(PreparedSendFile file)
    {
        var scopes = new List<IDisposable>
        {
            LogContext.PushProperty("TransferDirection", "Send"),
            LogContext.PushProperty("TransferMode", file.IsParsedPayload ? "Parsed" : "Raw"),
            LogContext.PushProperty("IsParsedPayload", file.IsParsedPayload),
            LogContext.PushProperty("SourceFilePath", file.SourcePath),
            LogContext.PushProperty("SourceFileName", Path.GetFileName(file.SourcePath)),
            LogContext.PushProperty("TransferFileName", file.DisplayFileName)
        };

        if (file.IsParsedPayload)
        {
            scopes.Add(LogContext.PushProperty("ParsedFileName", file.DisplayFileName));
            scopes.Add(LogContext.PushProperty("ParsedPayloadSize", file.ParsedPayload?.LongLength ?? 0));

            if (!string.IsNullOrWhiteSpace(file.ParserName))
            {
                scopes.Add(LogContext.PushProperty("ParserName", file.ParserName!));
            }

            if (file.ParsedSegmentCount.HasValue)
            {
                scopes.Add(LogContext.PushProperty("ParsedSegmentCount", file.ParsedSegmentCount.Value));
            }
        }

        return new CompositeScope(scopes);
    }

    private sealed class CompositeScope(IReadOnlyList<IDisposable> scopes) : IDisposable
    {
        public void Dispose()
        {
            for (var i = scopes.Count - 1; i >= 0; i--)
            {
                scopes[i].Dispose();
            }
        }
    }
}

public sealed record SendProgressSnapshot(
    long SentBytes,
    long TotalBytes,
    long SentPackets,
    long TotalPackets,
    long Status,
    string Message);

public sealed record ReceiveProgressSnapshot(
    long ReceivedBytes,
    long TotalBytes,
    long PacketNo,
    long TotalPacket,
    long Status,
    string Message,
    string FileName,
    string FileDate);
