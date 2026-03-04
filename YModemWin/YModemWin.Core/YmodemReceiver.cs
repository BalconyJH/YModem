namespace YModemWin.Core
{
    using System;
    using System.IO;
    using System.IO.Ports;
    using Sentry;
    using Serilog;
    using System.Text;
    
    public class YModemReceiver
    {
        private static readonly ILogger Logger = Log.ForContext<YModemReceiver>();
        private const int PacketSize128 = 128; // 128 字节包大小
        private const int PacketSize1024 = 1024; // 1024 字节包大小
        private const byte SOH = 0x01; // 128 字节包标识
        private const byte STX = 0x02; // 1024 字节包标识
        private const byte EOT = 0x04; // 结束传输标识
        private const byte ACK = 0x06; // 确认标识
        private const byte NAK = 0x15; // 否认标识
        private const byte CAN = 0x18; // 取消传输标识
        private const byte C = 0x43; // 'C' 字符，表示准备接收数据
        private const byte CTRLZ = 0x1A; // 填充字符

        private SerialPort serialPort; // 串口对象
        public string? saveFileName; // 文件保存路径
        public DateTime saveFileDate; //文件修改日期
        public string? saveFilePath; // 文件保存路径
        public long fileLength = 0;
        private string saveDirectory;
        private bool isTransmissionComplete;
        private long ReceivedLength = 0;
        DateTime dt;
        long status;
        private byte[]? packetBuffer;
        private long expectedPackageNo = 0;
        private long totalPackage = 0;
        
        //完成字节，总字节，文件名，文件日期
        Action<long, long, long, long, long, string, string, string>? RefreshReceiveUI=null;
        public YModemReceiver(SerialPort sp,bool timeout,string path, Action<long,long, long, long, long, string,string, string> action)
        {
            serialPort = sp;

            if (timeout)
            {
                serialPort.ReadTimeout = 3000;
            }else
            {
                serialPort.ReadTimeout = 8000;
            }
            saveDirectory = path;
            isTransmissionComplete = false;
            status = 0;
            expectedPackageNo = 0;
            RefreshReceiveUI = action;
        }

        public void StartReceiving()
        {
            var transaction = SentrySdk.StartTransaction("ymodem.receive", "serial.transfer");
            var transactionFinished = false;
            transaction.SetData("receiver.path", saveDirectory);

            status = 0;
            expectedPackageNo = 0;
            isTransmissionComplete = false;
            serialPort.DiscardInBuffer();
            dt = new DateTime(0);

            try
            {
                SendChar(C);

                while (!isTransmissionComplete)
                {
                    if (expectedPackageNo % 32 == 0)
                    {
                        Logger.Debug("Waiting for packet #{ExpectedPacketNo}", expectedPackageNo);
                    }
                    var receivePacketSpan = transaction.StartChild("serial.packet.receive", "receive_packet");
                    var packetLength = ReceivePacket();
                    receivePacketSpan.SetData("packet.length", packetLength);
                    receivePacketSpan.SetData("packet.expected", expectedPackageNo);
                    receivePacketSpan.Finish();

                    if (packetLength > 0)
                    {
                        if (packetBuffer == null) continue;
                        var packetType = packetBuffer[0];

                        if (packetType == SOH || packetType == STX)
                        {
                            if (dt.Ticks == 0)
                            {
                                dt = DateTime.Now;
                            }

                            if (expectedPackageNo == 0)
                            {
                                var rsvPackageNo = packetBuffer[1];
                                if (rsvPackageNo == 0)
                                {
                                    if (packetBuffer[3] == 0)
                                    {
                                        var finishSpan = transaction.StartChild("serial.transfer.complete", "all_files_completed");
                                        HandleFilesAllCompeted();
                                        finishSpan.Finish();
                                        isTransmissionComplete = true;
                                    }
                                    else
                                    {
                                        var metadataSpan = transaction.StartChild("serial.packet.parse", "metadata");
                                        ParseFileInfo(packetBuffer, packetLength);
                                        metadataSpan.Finish();
                                    }
                                }
                                else
                                {
                                    transaction.Finish(SpanStatus.InternalError);
                                    transactionFinished = true;
                                    Logger.Warning("Packet sequence mismatch detected");
                                    status = -1;
                                    isTransmissionComplete = true;
                                    RefreshReceiveUI?.Invoke(ReceivedLength, fileLength, expectedPackageNo, totalPackage, status, "包序号错误", saveFileName ?? "", saveFileDate.ToShortDateString());
                                }
                            }
                            else
                            {
                                var dataPacketSpan = transaction.StartChild("serial.packet.process", "data");
                                HandleDataPacket(packetLength, packetType);
                                dataPacketSpan.Finish();
                            }
                        }
                        else if (packetType == EOT)
                        {
                            var eotSpan = transaction.StartChild("serial.handshake", "eot");
                            HandleEndOfTransmission();
                            eotSpan.Finish();
                            continue;
                        }
                        else if (packetType == CAN)
                        {
                            transaction.Finish(SpanStatus.Aborted);
                            transactionFinished = true;
                            status = -1;
                            isTransmissionComplete = true;
                            RefreshReceiveUI?.Invoke(ReceivedLength, fileLength, expectedPackageNo, totalPackage, status, "Receive task was canceled by sender", saveFileName ?? "", saveFileDate.ToShortDateString());
                        }
                    }
                    else if (expectedPackageNo != 0)
                    {
                        transaction.Finish(SpanStatus.InternalError);
                        transactionFinished = true;
                        status = -1;
                        isTransmissionComplete = true;
                        RefreshReceiveUI?.Invoke(ReceivedLength, fileLength, expectedPackageNo, totalPackage, status, "数据接收超时", saveFileName ?? "", saveFileDate.ToShortDateString());
                    }
                    else
                    {
                        SendChar(C);
                    }
                }

                transaction.Finish(status == 1 ? SpanStatus.Ok : SpanStatus.InternalError);
                transactionFinished = true;
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                transaction.Finish(SpanStatus.InternalError);
                transactionFinished = true;
                status = -1;
                RefreshReceiveUI?.Invoke(ReceivedLength, fileLength, expectedPackageNo, totalPackage, status, ex.Message, saveFileName ?? "", saveFileDate.ToShortDateString());
            }
            finally
            {
                if (!transactionFinished)
                {
                    transaction.Finish();
                }
            }
        }

        public void StopReceiving()
        {
            isTransmissionComplete = true;
            SendChar(CAN);
            SendChar(CAN);
            SendChar(CAN);
            status = -2;
            RefreshReceiveUI?.Invoke(ReceivedLength, fileLength, expectedPackageNo, totalPackage, status, "Receive canceled by user", saveFileName ?? "", saveFileDate.ToShortDateString());

        }

        private int ReceivePacket()
        {
            var packetSize = 0;
            var headerLength = 3; // 3 字节头部 (类型, 包编号, 包编号的补码)
            var crcLength = 2; // 2 字节 CRC 校验

            while (true)
            {
                //System.Threading.Thread.Sleep(200);
                var readByte = -1;
                try
                {
                    readByte = serialPort.ReadByte();
                }
                catch
                { }
                if (readByte == -1)
                {
                    status = -1;
                    return -1; // 超时或错误
                }

                var packetType = (byte)readByte;

                if (packetType == SOH)
                {
                    packetSize = PacketSize128;
                }
                else if (packetType == STX)
                {
                    packetSize = PacketSize1024;
                }
                else if (packetType == EOT)
                {
                    packetBuffer = new byte[1];
                    packetBuffer[0] = packetType;
                    return 1;

                }
                else if (packetType == CAN)
                {
                    packetBuffer = new byte[1];
                    packetBuffer[0] = packetType;
                    return 1;

                }
                else
                {
                    continue;
                }

                packetBuffer = new byte[packetSize + headerLength + crcLength];
                packetBuffer[0] = packetType;
                break;
            }

            var bytesRead = 1;
            while (bytesRead < packetBuffer.Length)
            {
                var read = serialPort.Read(packetBuffer, bytesRead, packetBuffer.Length - bytesRead);
                bytesRead += read;

                if (bytesRead == 0)
                {
                    status = -1;
                    RefreshReceiveUI?.Invoke(ReceivedLength, fileLength, expectedPackageNo, totalPackage, status, "数据接收超时", saveFileName ?? "", saveFileDate.ToShortDateString());
                    return -1; // 超时或错误
                }
                }
            status = 2;
            return bytesRead;
        }

        private void ParseFileInfo(byte[] buffer, int packetLength)
        {
            // 提取文件名
            var nameEndIndex = Array.IndexOf(buffer, (byte)0, 3);
            var fileName = Encoding.GetEncoding("gb2312").GetString(buffer, 3, nameEndIndex - 3);
            saveFileName = fileName;

            // 提取文件扩展信息
            var extendedStartIndex = nameEndIndex + 1;
            var extendedEndIndex = Array.IndexOf(buffer, (byte)0, extendedStartIndex);
            var infoString = Encoding.ASCII.GetString(buffer, extendedStartIndex, extendedEndIndex - extendedStartIndex);
            var infoParts = infoString.Split(' ');

            // 生成保存文件路径
            saveFilePath = Path.Combine(saveDirectory, fileName);
            if (File.Exists(saveFilePath)) File.Delete(saveFilePath);

            if (infoParts.Length >= 1)
            {
                // 解析文件长度
                if (long.TryParse(infoParts[0], out fileLength))
                {
                    Logger.Information("Incoming file size: {FileLength} bytes", fileLength);
                }
                else
                {
                    throw new InvalidOperationException("无法解析文件长度");
                }
            }

            if (infoParts.Length >= 2)
            {
                // 解析修改日期
                var octalDateString = infoParts[1];
                if (octalDateString != "0" && !string.IsNullOrEmpty(octalDateString))
                {
                    try
                    {
                        var secondsSinceEpoch = Convert.ToInt64(octalDateString, 8);
                        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                        saveFileDate = epoch.AddSeconds(secondsSinceEpoch);
                        Logger.Debug("Parsed file timestamp: {FileTimestamp}", saveFileDate);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "Failed to parse file timestamp, fallback to current UTC time");
                        saveFileDate = DateTime.UtcNow; // 如果解析出错，则使用接收日期
                    }
                }
                else
                {
                    saveFileDate = DateTime.UtcNow; // 如果日期为0，则使用当前日期
                    Logger.Debug("File timestamp missing, fallback timestamp: {FallbackTimestamp}", saveFileDate);
                }
            }

            if (infoParts.Length >= 3)
            {
                // 解析序列号（八进制）
                var serialNumberString = infoParts[2];
                if (!string.IsNullOrEmpty(serialNumberString))
                {
                    try
                    {
                        var serialNumber = Convert.ToInt32(serialNumberString, 8);
                        totalPackage = serialNumber;
                        Logger.Debug("Parsed file sequence number: {SerialNumber}", serialNumber);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"无法解析文件序列号: {ex.Message}");
                    }
                }
            }

            if (infoParts.Length == 0)
            {
                throw new InvalidOperationException("文件信息包格式不正确");
            }

            System.Threading.Thread.Sleep(100);
            SendChar(ACK); // 发送确认信号
            System.Threading.Thread.Sleep(300);
            SendChar(C); // 请求数据传输
            expectedPackageNo++;
        }


        private void HandleDataPacket(int packetLength, byte packetType)
        {
            if (packetBuffer == null) return;
            
            var packetNum = packetBuffer[1];
            var inversePacketNum = packetBuffer[2];
            var dataLength = packetType == SOH ? PacketSize128 : PacketSize1024;
            var data = new byte[dataLength];
            Array.Copy(packetBuffer, 3, data, 0, data.Length);

            var receivedCrc = (ushort)((packetBuffer[packetLength - 2] << 8) | packetBuffer[packetLength - 1]);
            var calculatedCrc = CalculateCrc16(data);

            if (packetNum + inversePacketNum == 0xFF && receivedCrc == calculatedCrc && packetNum==expectedPackageNo%256)
            {
                SaveDataToFile(data);
                SendChar(ACK);
                RefreshReceiveUI?.Invoke(ReceivedLength, fileLength, expectedPackageNo, totalPackage, status, "正在接收文件" + saveFileName, saveFileName ?? "", saveFileDate.ToShortDateString()) ;
                expectedPackageNo++;
            }
            else
            {
                SendChar(NAK);
            }

            
        }

        private void HandleEndOfTransmission()
        {
            // 回复 NAK，等待确认 EOT
            SendChar(NAK);
            var packetLength = ReceivePacket();
            if (packetLength > 0 && packetBuffer != null && packetBuffer[0] == EOT)
            {
                SendChar(ACK); // 确认接收到 EOT
                SendChar(C); // 请求下一个传输（如果有）
                expectedPackageNo = 0;
                ReceivedLength = 0;
            }else
            {
                status = -1;
                RefreshReceiveUI?.Invoke(ReceivedLength, fileLength, expectedPackageNo, totalPackage, status, "终止传输指令未正确响应", saveFileName ?? "", saveFileDate.ToShortDateString());
            }
        }
        private void HandleFilesAllCompeted()
        {
            if (packetBuffer == null) return;
            
            if (packetBuffer[4]==0x30 && packetBuffer[5] == 0x20 && packetBuffer[6] == 0x30 && packetBuffer[7] == 0x20 && packetBuffer[8] == 0x30)
            {
                isTransmissionComplete = true; // 结束传输
                SendChar(ACK); // 发送确认信号
                var span = DateTime.Now - dt;
                status = 1;
                //MessageBox.Show("接收耗时:" + span.TotalMilliseconds.ToString() + "毫秒", "接收成功", MessageBoxButtons.OK, MessageBoxIcon.None);
                RefreshReceiveUI?.Invoke(ReceivedLength, fileLength, expectedPackageNo, totalPackage, status, "接收成功，耗时:" + span.TotalSeconds.ToString() + "秒", saveFileName ?? "", saveFileDate.ToShortDateString());
            }
        }

        private void SaveDataToFile(byte[] data)
        {
            if (saveFilePath == null) return;
            
            using (var fileStream = new FileStream(saveFilePath, FileMode.Append))
            {
                var datelen = data.Length;
                var actualLength = Array.IndexOf(data, CTRLZ);
                if (actualLength > 0 && (fileLength-ReceivedLength)< datelen)
                {
                    if ((fileLength - ReceivedLength) > actualLength) actualLength =(int)(fileLength - ReceivedLength);
                    fileStream.Write(data, 0, actualLength);
                    ReceivedLength = ReceivedLength + actualLength;
                }
                else
                {
                    fileStream.Write(data, 0, data.Length);
                    ReceivedLength = ReceivedLength + data.Length;
                }
            }
        }

        private ushort CalculateCrc16(byte[] data)
        {
            const ushort polynomial = 0x1021;
            ushort crc = 0;

            foreach (var b in data)
            {
                crc ^= (ushort)(b << 8);
                for (var i = 0; i < 8; i++)
                {
                    if ((crc & 0x8000) != 0)
                    {
                        crc = (ushort)((crc << 1) ^ polynomial);
                    }
                    else
                    {
                        crc <<= 1;
                    }
                }
            }

            return crc;
        }

        private void SendChar(byte c)
        {
            if (serialPort.IsOpen) serialPort.Write(new byte[] { c }, 0, 1);
        }
    }



}
