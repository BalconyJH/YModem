using System.IO.Ports;
using System.Text;
using Serilog;

namespace YModemWin.Core
{
    public class YModemTransmitter
    {
        private static readonly ILogger Logger = Log.ForContext<YModemTransmitter>();
        /* 控制信号 */
        const byte SOH = 1; // 128字节包开头
        const byte STX = 2; // 1024字节包开头
        const byte EOT = 4; // 传输结束
        const byte ACK = 6; // 确认信号
        const byte NAK = 0x15; // 否认信号
        const byte C = 0x43; // 请求数据

        const byte CAN = 0x18; // 取消传输标识

        /* 尺寸 */
        public const int DataSize = 1024;
        public const int CrcSize = 2; // CRC校验的大小
        
        private const int CancelCheckIntervalMs = 200; // 取消检查间隔
        private const int MaxRetryCount = 10; // 单个数据包最大重试次数
        
        SerialPort serialPort;
        private int originalReadTimeout;
        string? Path;
        int packagesent;
        int totalpackage;
        long status;
        bool isTramsitting;
        bool userCancel = false;
        public DateTime dt = new DateTime(0);

        //完成包号，总包号，文件名
        Action<long, long, long, long, long, string>? RefreshSendUI = null;

        public YModemTransmitter(SerialPort sp, int timeoutSeconds, Action<long, long, long, long, long, string> action)
        {
            status = 0;
            serialPort = sp;
            RefreshSendUI = action;
            dt = new DateTime(0);
            originalReadTimeout = timeoutSeconds <= 0 ? 1000000 : timeoutSeconds * 1000;
            serialPort.ReadTimeout = originalReadTimeout;
        }
        
        /// <summary>
        /// 可取消的读取单个字节，定期检查取消标志
        /// </summary>
        /// <returns>读取的字节，如果取消则返回 -1</returns>
        private int ReadByteWithCancel()
        {
            var elapsed = 0;
            serialPort.ReadTimeout = CancelCheckIntervalMs;
            
            try
            {
                while (!userCancel && elapsed < originalReadTimeout)
                {
                    try
                    {
                        return serialPort.ReadByte();
                    }
                    catch (TimeoutException)
                    {
                        elapsed += CancelCheckIntervalMs;
                    }
                }
                
                return userCancel ? -1 : -2; // -1 = 用户取消, -2 = 超时
            }
            finally
            {
                serialPort.ReadTimeout = originalReadTimeout;
            }
        }

        //支持多文件传输，如果是仅发送一个文件，或者是多个文件的最后一个文件，输入参数isLastFile默认为真
        public bool YmodemSendFile(string path, bool isLastFile = true)
        {
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var fileName = System.IO.Path.GetFileName(path);
            var lastWriteTime = File.GetLastWriteTime(path);
            return YmodemSendStream(fileStream, fileName, lastWriteTime, isLastFile);
        }

        public bool YmodemSendParsedData(string originalFileName, DateTime lastWriteTime, byte[] payload, bool isLastFile = true)
        {
            using var memoryStream = new MemoryStream(payload, writable: false);
            // 将文件名后缀改为 .bin
            var binFileName = System.IO.Path.ChangeExtension(originalFileName, ".bin");
            return YmodemSendStream(memoryStream, binFileName, lastWriteTime, isLastFile);
        }

        private bool YmodemSendStream(Stream fileStream, string fileName, DateTime lastWriteTime, bool isLastFile)
        {
            // 如果已经取消，直接返回
            if (userCancel)
            {
                status = -2;
                RefreshSendUI?.Invoke(0, fileStream.Length, 0, 0, status, "Send canceled by user.");
                return false;
            }
            
            isTramsitting = true;
            Path = fileName;
            var transaction = SentrySdk.StartTransaction("ymodem.send", "serial.transfer");
            var transactionFinished = false;
            transaction.SetTag("ymodem.mode", isLastFile ? "single-or-last" : "multi");
            transaction.SetTag("ymodem.file_name", fileName);
            transaction.SetData("ymodem.file_size", fileStream.Length);
            totalpackage = (int)(fileStream.Length - 1) / YModemTransmitter.DataSize + 1;
            packagesent = 0;
            Logger.Information("Prepared transfer with {TotalPacketCount} packet(s)", totalpackage);

            var invertedPacketNumber = 255;
            var data = new byte[DataSize];
            var CRC = new byte[CrcSize];

            var crc16Ccitt = new Crc16Ccitt(InitialCrcValue.Zeros);
            var packetNumber = 0;
            Thread.Sleep(1);

            try
            {
                var waitReceiverReadySpan = transaction.StartChild("serial.handshake", "wait_receiver_ready");
                while (isTramsitting && !userCancel)
                {
                    var ret = -1;
                    try
                    {
                        serialPort.ReadTimeout = CancelCheckIntervalMs;
                        ret = serialPort.ReadByte();
                    }
                    catch (TimeoutException)
                    {
                        // 超时继续等待
                    }
                    catch
                    {
                        // 其他异常退出
                        break;
                    }
                    finally
                    {
                        serialPort.ReadTimeout = originalReadTimeout;
                    }

                    if (ret == C) break;
                    if (userCancel) break;
                }

                waitReceiverReadySpan.Finish();
                
                if (userCancel)
                {
                    transaction.Finish(SpanStatus.Cancelled);
                    transactionFinished = true;
                    status = -2;
                    RefreshSendUI?.Invoke(0, fileStream.Length, 0, totalpackage, status, "Send canceled by user.");
                    return false;
                }

                serialPort.DiscardInBuffer();
                if (dt.Ticks == 0) dt = DateTime.Now;

                var metadataPacketSpan = transaction.StartChild("serial.packet.send", "initial_metadata_packet");
                sendYmodemInitialPacket(STX, packetNumber, invertedPacketNumber, data, DataSize, fileName, fileStream.Length, lastWriteTime, CRC, CrcSize);
                var read = ReadByteWithCancel();
                metadataPacketSpan.Finish();
                
                if (userCancel || read < 0)
                {
                    transaction.Finish(SpanStatus.Cancelled);
                    transactionFinished = true;
                    status = -2;
                    RefreshSendUI?.Invoke(0, fileStream.Length, 0, totalpackage, status, "Send canceled by user.");
                    return false;
                }
                
                if (read != ACK)
                {
                    transaction.Finish(SpanStatus.InternalError);
                    transactionFinished = true;
                    RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status,
                        "Failed to send initial metadata packet");
                    status = -1;
                    return false;
                }

                var cRead = ReadByteWithCancel();
                if (userCancel || cRead < 0 || cRead != C)
                {
                    if (userCancel)
                    {
                        transaction.Finish(SpanStatus.Cancelled);
                        transactionFinished = true;
                        status = -2;
                        RefreshSendUI?.Invoke(0, fileStream.Length, 0, totalpackage, status, "Send canceled by user.");
                        return false;
                    }
                    transaction.Finish(SpanStatus.InternalError);
                    transactionFinished = true;
                    RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status,
                        "Did not receive expected receiver request");
                    status = -1;
                    return false;
                }

                int packageReadCount;
                var retryCount = 0; // 当前包的重试次数
                long packetStartPosition = 0;
                do
                {
                    if (userCancel)
                    {
                        transaction.Finish(SpanStatus.Cancelled);
                        transactionFinished = true;
                        status = -2;
                        RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status, "Send canceled by user.");
                        return false;
                    }
                    
                    packetStartPosition = fileStream.Position;
                    packageReadCount = fileStream.Read(data, 0, DataSize);
                    if (packageReadCount == 0) break;
                    if (packageReadCount != DataSize)
                        for (var i = packageReadCount; i < DataSize; i++)
                            data[i] = 0x1A;

                    packetNumber++;
                    packagesent++;
                    if (packetNumber > 255)
                        packetNumber -= 256;
                    RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packagesent, totalpackage, status,
                        "Sending file " + fileName);

                    if (packetNumber % 32 == 0 || packetNumber == 1)
                    {
                        Logger.Debug("Transmitted packet {PacketNumber}/{TotalPacketCount}", packagesent, totalpackage);
                    }

                    invertedPacketNumber = 255 - packetNumber;
                    CRC = crc16Ccitt.ComputeChecksumBytes(data);

                    var dataPacketSpan = transaction.StartChild("serial.packet.send", "data_packet");
                    dataPacketSpan.SetData("packet.number", packetNumber);
                    sendYmodemPacket(STX, packetNumber, invertedPacketNumber, data, DataSize, CRC, CrcSize);

                    var signal = ReadByteWithCancel();
                    dataPacketSpan.SetData("packet.signal", signal);
                    dataPacketSpan.Finish();
                    
                    if (userCancel || signal == -1)
                    {
                        try
                        {
                            serialPort.Write(new byte[] { CAN, CAN, CAN, CAN, CAN, CAN, CAN, CAN }, 0, 8);
                        }
                        catch { }
                        transaction.Finish(SpanStatus.Cancelled);
                        transactionFinished = true;
                        status = -2;
                        RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage,
                            status, "Send canceled by user.");
                        return false;
                    }
                    
                    if (signal == -2)
                    {
                        transaction.Finish(SpanStatus.InternalError);
                        transactionFinished = true;
                        status = -1;
                        RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage,
                            status, "Receiver timeout");
                        return false;
                    }
                    
                    if (signal == ACK)
                    {
                        status = 2;
                        retryCount = 0; // 发送成功，重置重试计数
                    }
                    else if (signal == NAK)
                    {
                        retryCount++;
                        if (retryCount >= MaxRetryCount)
                        {
                            // 达到最大重试次数，发送 CAN 取消传输
                            try
                            {
                                serialPort.Write(new byte[] { CAN, CAN, CAN, CAN, CAN, CAN, CAN, CAN }, 0, 8);
                            }
                            catch { }
                            transaction.Finish(SpanStatus.InternalError);
                            transactionFinished = true;
                            status = -1;
                            RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage,
                                status, $"Max retry count ({MaxRetryCount}) exceeded for packet {packagesent}. Transfer aborted.");
                            Logger.Error("Max retry count exceeded for packet {PacketNumber}, aborting transfer", packagesent);
                            return false;
                        }
                        
                        RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage,
                            status, $"Data transfer error, resending packet (retry {retryCount}/{MaxRetryCount}).");
                        Logger.Warning("Data transfer error detected, resending packet {PacketNumber} (retry {RetryCount}/{MaxRetryCount})", 
                            packetNumber, retryCount, MaxRetryCount);
                        status = -1;
                        if (fileStream.CanSeek)
                        {
                            // Roll back to the exact start of the current packet; fixed -1024 can underflow on small/last packets.
                            fileStream.Position = packetStartPosition;
                        }
                        else
                        {
                            transaction.Finish(SpanStatus.InternalError);
                            transactionFinished = true;
                            RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage,
                                status, "Stream is not seekable, cannot retry packet.");
                            Logger.Error("Cannot retry packet because stream does not support seeking");
                            return false;
                        }
                        packagesent = Math.Max(0, packagesent - 1);
                        packetNumber = packetNumber == 0 ? 255 : packetNumber - 1;
                    }
                    else if (signal == CAN)
                    {
                        transaction.Finish(SpanStatus.Aborted);
                        transactionFinished = true;
                        status = -1;
                        RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage,
                            status, "Send task was canceled by receiver.");
                        Logger.Warning("Packet send failed or was canceled by receiver");
                        return false;
                    }
                    else
                    {
                        transaction.Finish(SpanStatus.InternalError);
                        transactionFinished = true;
                        status = -1;
                        RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage,
                            status, "Receiver did not respond to sent packet.");
                        Logger.Warning("Packet send failed or was canceled by receiver");
                        return false;
                    }
                } while (DataSize == packageReadCount && isTramsitting && !userCancel);
                
                if (userCancel)
                {
                    try
                    {
                        serialPort.Write(new byte[] { CAN, CAN, CAN, CAN, CAN, CAN, CAN, CAN }, 0, 8);
                    }
                    catch { }
                    transaction.Finish(SpanStatus.Cancelled);
                    transactionFinished = true;
                    status = -2;
                    RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage,
                        status, "Send canceled by user.");
                    return false;
                }

                serialPort.Write(new byte[] { EOT }, 0, 1);

                var act = ReadByteWithCancel();
                if (userCancel || act < 0)
                {
                    transaction.Finish(SpanStatus.Cancelled);
                    transactionFinished = true;
                    status = -2;
                    RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage,
                        status, "Send canceled by user.");
                    return false;
                }
                
                if ((act != ACK) && (act != NAK))
                {
                    transaction.Finish(SpanStatus.InternalError);
                    transactionFinished = true;
                    RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status,
                        "Receiver did not respond correctly to end request.");
                    Logger.Warning("Unable to complete transfer during EOT handshake");
                    status = -1;
                    return false;
                }

                if (act == NAK)
                {
                    serialPort.Write(new byte[] { EOT }, 0, 1);
                }

                var ackRead = ReadByteWithCancel();
                if (userCancel || ackRead < 0 || ackRead != ACK)
                {
                    if (userCancel)
                    {
                        transaction.Finish(SpanStatus.Cancelled);
                        transactionFinished = true;
                        status = -2;
                        RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage,
                            status, "Send canceled by user.");
                        return false;
                    }
                    transaction.Finish(SpanStatus.InternalError);
                    transactionFinished = true;
                    RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status,
                        "Receiver did not respond correctly to end request.");
                    Logger.Warning("Unable to complete transfer during EOT handshake");
                    status = -1;
                    return false;
                }

                if (isLastFile)
                {
                    var cReadFinal = ReadByteWithCancel();
                    if (userCancel || cReadFinal < 0 || cReadFinal != C)
                    {
                        if (userCancel)
                        {
                            transaction.Finish(SpanStatus.Cancelled);
                            transactionFinished = true;
                            status = -2;
                            RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage,
                                status, "Send canceled by user.");
                            return false;
                        }
                        transaction.Finish(SpanStatus.InternalError);
                        transactionFinished = true;
                        RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage,
                            status, "Receiver did not respond correctly to end request.");
                        Logger.Warning("Unable to complete transfer during EOT handshake");
                        status = -1;
                        return false;
                    }

                    packetNumber = 0;
                    invertedPacketNumber = 255;
                    data = new byte[128];
                    data[0] = 0x00;
                    data[1] = data[3] = data[5] = 0x30;
                    data[2] = data[4] = 0x20;
                    CRC = new byte[CrcSize];
                    CRC = crc16Ccitt.ComputeChecksumBytes(data);

                    sendYmodemClosingPacket(SOH, packetNumber, invertedPacketNumber, data, 128, CRC, CrcSize);

                    var finalAck = ReadByteWithCancel();
                    if (userCancel || finalAck < 0 || finalAck != ACK)
                    {
                        if (userCancel)
                        {
                            transaction.Finish(SpanStatus.Cancelled);
                            transactionFinished = true;
                            status = -2;
                            RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage,
                                status, "Send canceled by user.");
                            return false;
                        }
                        transaction.Finish(SpanStatus.InternalError);
                        transactionFinished = true;
                        Logger.Warning("Unable to complete transfer during EOT handshake");
                        RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage,
                            status, "Receiver did not respond correctly to end request.");
                        status = -1;
                        return false;
                    }

                    Logger.Information("File transfer completed successfully");
                    var span = DateTime.Now - dt;
                    status = 1;
                    RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packagesent, totalpackage, status,
                        "Send completed, elapsed: " + span.TotalSeconds.ToString() + "s");
                }

                transaction.Finish(SpanStatus.Ok);
                transactionFinished = true;
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                transaction.Finish(SpanStatus.InternalError);
                transactionFinished = true;
                status = -1;
                RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status,
                    "Receiver timeout");
                return false;
            }
            finally
            {
                if (!transactionFinished)
                {
                    transaction.Finish();
                }
            }

            return true;
        }

        public void StopTransmitting()
        {
            userCancel = true;
            isTramsitting = false;
        }
        
        /// <summary>
        /// 重置取消状态，在新传输任务开始前调用
        /// </summary>
        public void ResetCancel()
        {
            userCancel = false;
        }

        /// <summary>
        /// 发送多个文件
        /// </summary>
        /// <param name="files"></param>
        public void YmodemSendFiles(List<string> files)
        {
            for (var i = 0; i < files.Count; i++)
            {
                if (userCancel) break;
                
                bool success;
                if (i != files.Count - 1)
                {
                    success = YmodemSendFile(files[i], false);
                }
                else
                {
                    success = YmodemSendFile(files[i]);
                }

                // 如果发送失败或取消，退出循环
                if (!success || userCancel) break;
            }
        }

        private void sendYmodemInitialPacket(byte STX, int packetNumber, int invertedPacketNumber, byte[] data,
            int dataSize, string fileName, long fileLength, DateTime lastWriteTime, byte[] CRC, int crcSize)
        {
            // YModem协议不允许字符串中出现空格，将空格替换为下划线
            fileName = fileName.Replace(" ", "_");
            var fileSize = fileLength.ToString();

            // Compute Unix timestamp manually (from 1970-01-01 to lastWriteTime in seconds)
            var epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var unixTime = (lastWriteTime.ToUniversalTime().Ticks - epoch.Ticks) / TimeSpan.TicksPerSecond;

            // 将Unix时间戳转换为八进制字符串
            var fileModTime = Convert.ToString(unixTime, 8);


            // 将包数转换为八进制字符串
            var packageCount = Convert.ToString(totalpackage, 8);

            // 使用 Encoding 类中的 GetBytes 方法将字符串转换为 GB2312 编码的字节数组
            var gb2312Bytes = Encoding.GetEncoding("gb2312").GetBytes(fileName);

            /* 将文件名添加到数据中 */
            int i;
            for (i = 0; i < gb2312Bytes.Length && (gb2312Bytes[i] != 0); i++)
            {
                data[i] = (byte)gb2312Bytes[i];
            }

            data[i] = 0;
            /* 将文件大小添加到数据中 */
            int j;
            for (j = 0; j < fileSize.Length && (fileSize.ToCharArray()[j] != 0); j++)
            {
                data[i + j + 1] = (byte)fileSize.ToCharArray()[j];
            }

            data[i + j + 1] = (byte)(' ');
            /* 将文件修改时间添加到数据中 */
            int m;
            for (m = 0; m < fileModTime.Length && (fileModTime.ToCharArray()[m] != 0); m++)
            {
                data[i + j + m + 2] = (byte)fileModTime.ToCharArray()[m];
            }

            data[i + j + m + 2] = (byte)(' ');
            /* 将文件修改时间添加到数据中 */
            int n;
            for (n = 0; n < packageCount.Length && (packageCount.ToCharArray()[n] != 0); n++)
            {
                data[i + j + n + m + 3] = (byte)packageCount.ToCharArray()[n];
            }

            data[i + j + m + n + 3] = (byte)(' ');
            /* 用0填充剩余的数据字节 */
            for (var k = (i + j + m + n + 4); k < dataSize; k++)
            {
                data[k] = 0;
            }

            /* 计算CRC校验码 */
            var crc16Ccitt = new Crc16Ccitt(InitialCrcValue.Zeros);
            CRC = crc16Ccitt.ComputeChecksumBytes(data);

            /* 发送数据包 */
            sendYmodemPacket(STX, packetNumber, invertedPacketNumber, data, dataSize, CRC, crcSize);
        }

        private void sendYmodemClosingPacket(byte SOH, int packetNumber, int invertedPacketNumber, byte[] data,
            int dataSize, byte[] CRC, int crcSize)
        {
            /* 计算CRC校验码 */
            var crc16Ccitt = new Crc16Ccitt(InitialCrcValue.Zeros);
            CRC = crc16Ccitt.ComputeChecksumBytes(data);

            /* 发送数据包 */
            sendYmodemPacket(SOH, packetNumber, invertedPacketNumber, data, dataSize, CRC, crcSize);
        }

        private void sendYmodemPacket(byte STX, int packetNumber, int invertedPacketNumber, byte[] data, int dataSize,
            byte[] CRC, int crcSize)
        {
            var packetSize = 1 + 1 + 1 + dataSize + crcSize; // 计算包的总大小

            // 创建一个足够大的字节数组来存储整个包
            var packet = new byte[packetSize];

            // 填充包数据
            packet[0] = STX; // STX
            packet[1] = (byte)packetNumber; // Packet Number
            packet[2] = (byte)invertedPacketNumber; // Inverted Packet Number
            Array.Copy(data, 0, packet, 3, dataSize); // 复制数据到包中
            Array.Copy(CRC, 0, packet, 3 + dataSize, crcSize); // 复制CRC到包中

            // 通过串口一次性发送整个包
            serialPort.Write(packet, 0, packet.Length);
        }
    }
}