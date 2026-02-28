using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sentry;

namespace YModemWin
{
    public partial class YModemTransmitter
    {
        /* 控制信号 */
        const byte SOH = 1;  // 128字节包开头
        const byte STX = 2;  // 1024字节包开头
        const byte EOT = 4;  // 传输结束
        const byte ACK = 6;  // 确认信号
        const byte NAK = 0x15;  // 否认信号
        const byte C = 0x43;   // 请求数据
        const byte CAN = 0x18; // 取消传输标识
        /* 尺寸 */
        public const int DataSize = 1024;
        public const int CrcSize = 2;  // CRC校验的大小
        SerialPort serialPort;
        string Path;
        int packagesent;
        int totalpackage;
        long status;
        bool isTramsitting;
        bool userCancel = false;
        public DateTime dt=new DateTime(0);

        //完成包号，总包号，文件名
        Action<long, long,long,long,long,string> RefreshSendUI=null;

        public YModemTransmitter(SerialPort sp,bool timeout, Action<long, long, long, long, long,string> action)
        {
            status = 0;
            serialPort = sp;
            RefreshSendUI = action;
            dt = new DateTime(0);
            if (timeout)
            {
                serialPort.ReadTimeout = 3000;
            }else
            {
                serialPort.ReadTimeout = 1000000;
            }
        }

        //支持多文件传输，如果是仅发送一个文件，或者是多个文件的最后一个文件，输入参数isLastFile默认为真
        public bool YmodemSendFile(string path, bool isLastFile = true)
        {
            userCancel = false;
            isTramsitting = true;
            Path = path;

            var transaction = SentrySdk.StartTransaction("ymodem.send", "serial.transfer");
            var transactionFinished = false;
            transaction.SetTag("ymodem.mode", isLastFile ? "single-or-last" : "multi");
            transaction.SetData("ymodem.file_name", System.IO.Path.GetFileName(path));

            int invertedPacketNumber = 255;
            byte[] data = new byte[DataSize];
            byte[] CRC = new byte[CrcSize];
            Crc16Ccitt crc16Ccitt = new Crc16Ccitt(InitialCrcValue.Zeros);
            int packetNumber = 0;
            Thread.Sleep(1);

            try
            {
                using var fileStream = new FileStream(@path, FileMode.Open, FileAccess.Read);
                transaction.SetData("ymodem.file_size", fileStream.Length);

                totalpackage = (int)(fileStream.Length - 1) / YModemTransmitter.DataSize + 1;
                Console.WriteLine("total section len=" + totalpackage.ToString());

                var waitReceiverReadySpan = transaction.StartChild("serial.handshake", "wait_receiver_ready");
                try
                {
                    while (isTramsitting)
                    {
                        int ret = -1;
                        try
                        {
                            ret = serialPort.ReadByte();
                        }
                        catch
                        {
                        }

                        if (ret == C)
                        {
                            break;
                        }

                        Thread.Sleep(30);
                    }
                }
                finally
                {
                    waitReceiverReadySpan.Finish();
                }

                serialPort.DiscardInBuffer();
                if (dt.Ticks == 0)
                {
                    dt = DateTime.Now;
                }

                byte read;
                var metadataPacketSpan = transaction.StartChild("serial.packet.send", "initial_metadata_packet");
                try
                {
                    sendYmodemInitialPacket(STX, packetNumber, invertedPacketNumber, data, DataSize, Path, fileStream, CRC, CrcSize);
                    read = (byte)serialPort.ReadByte();
                    metadataPacketSpan.SetData("packet.signal", read);
                }
                finally
                {
                    metadataPacketSpan.Finish();
                }

                if (read != ACK)
                {
                    transaction.Finish(SpanStatus.InternalError);
                    transactionFinished = true;
                    RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status, "发送初始数据包错误");
                    status = -1;
                    return false;
                }

                if (serialPort.ReadByte() != C)
                {
                    transaction.Finish(SpanStatus.InternalError);
                    transactionFinished = true;
                    RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status, "未接收到正确的接收请求");
                    status = -1;
                    return false;
                }

                int packageReadCount;
                do
                {
                    packageReadCount = fileStream.Read(data, 0, DataSize);
                    if (packageReadCount == 0)
                    {
                        break;
                    }

                    if (packageReadCount != DataSize)
                    {
                        for (int i = packageReadCount; i < DataSize; i++)
                        {
                            data[i] = 0x1A;
                        }
                    }

                    packetNumber++;
                    packagesent++;
                    if (packetNumber > 255)
                    {
                        packetNumber -= 256;
                    }

                    string fileName = System.IO.Path.GetFileName(path);
                    RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packagesent, totalpackage, status, "正在发送文件 " + fileName);

                    invertedPacketNumber = 255 - packetNumber;
                    CRC = crc16Ccitt.ComputeChecksumBytes(data);

                    bool shouldTracePacket = packetNumber == 1
                        || packagesent == totalpackage
                        || (packagesent % 100) == 0;

                    int signal;
                    ISpan? dataPacketSpan = null;
                    try
                    {
                        if (shouldTracePacket)
                        {
                            dataPacketSpan = transaction.StartChild("serial.packet.send", "data_packet");
                            dataPacketSpan.SetData("packet.number", packetNumber);
                        }

                        sendYmodemPacket(STX, packetNumber, invertedPacketNumber, data, DataSize, CRC, CrcSize);
                        signal = serialPort.ReadByte();
                        dataPacketSpan?.SetData("packet.signal", signal);
                    }
                    finally
                    {
                        dataPacketSpan?.Finish();
                    }

                    if (signal == ACK)
                    {
                        status = 2;
                    }
                    else if (signal == NAK)
                    {
                        RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status, "数据传输错误，重发数据包。");
                        Console.WriteLine("数据传输错误，重发数据包。");
                        status = -1;
                        fileStream.Position -= DataSize;
                        packagesent--;
                        packetNumber--;
                    }
                    else if (signal == CAN)
                    {
                        transaction.Finish(SpanStatus.Aborted);
                        transactionFinished = true;
                        status = -1;
                        RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status, "发送任务被接收端取消了。");
                        Console.WriteLine("无法发送数据包。");
                        return false;
                    }
                    else
                    {
                        transaction.Finish(SpanStatus.InternalError);
                        transactionFinished = true;
                        status = -1;
                        RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status, "接收端未响应发送的数据包。");
                        Console.WriteLine("无法发送数据包。");
                        return false;
                    }

                    if (userCancel)
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            serialPort.Write(new byte[] { CAN }, 0, 1);
                        }

                        transaction.Finish(SpanStatus.Cancelled);
                        transactionFinished = true;
                        status = -2;
                        RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status, "用户取消发送。");
                        return false;
                    }
                } while (DataSize == packageReadCount && isTramsitting);

                serialPort.Write(new byte[] { EOT }, 0, 1);

                int act = serialPort.ReadByte();
                if ((act != ACK) && (act != NAK))
                {
                    transaction.Finish(SpanStatus.InternalError);
                    transactionFinished = true;
                    RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status, "接收端未正确响应结束请求。");
                    Console.WriteLine("无法完成传输。");
                    status = -1;
                    return false;
                }

                if (act == NAK)
                {
                    serialPort.Write(new byte[] { EOT }, 0, 1);
                }

                if (serialPort.ReadByte() != ACK)
                {
                    transaction.Finish(SpanStatus.InternalError);
                    transactionFinished = true;
                    RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status, "接收端未正确响应结束请求。");
                    Console.WriteLine("无法完成传输。");
                    status = -1;
                    return false;
                }

                if (isLastFile)
                {
                    if (serialPort.ReadByte() != C)
                    {
                        transaction.Finish(SpanStatus.InternalError);
                        transactionFinished = true;
                        RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status, "接收端未正确响应结束请求。");
                        Console.WriteLine("无法完成传输。");
                        status = -1;
                        return false;
                    }

                    packetNumber = 0;
                    invertedPacketNumber = 255;
                    data = new byte[128];
                    data[0] = 0x00;
                    data[1] = data[3] = data[5] = 0x30;
                    data[2] = data[4] = 0x20;
                    CRC = crc16Ccitt.ComputeChecksumBytes(data);

                    sendYmodemClosingPacket(SOH, packetNumber, invertedPacketNumber, data, 128, CRC, CrcSize);

                    if (serialPort.ReadByte() != ACK)
                    {
                        transaction.Finish(SpanStatus.InternalError);
                        transactionFinished = true;
                        Console.WriteLine("无法完成传输。");
                        RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packetNumber, totalpackage, status, "接收端未正确响应结束请求。");
                        status = -1;
                        return false;
                    }

                    Console.WriteLine("文件传输成功");
                    TimeSpan span = DateTime.Now - dt;
                    status = 1;
                    RefreshSendUI?.Invoke(fileStream.Position, fileStream.Length, packagesent, totalpackage, status, "发送成功，耗时:" + span.TotalSeconds.ToString() + "秒");
                }

                transaction.Finish(SpanStatus.Ok);
                transactionFinished = true;
                return true;
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                transaction.Finish(SpanStatus.InternalError);
                transactionFinished = true;
                Console.WriteLine("接收方超时");
                status = -1;
                RefreshSendUI?.Invoke(0, 0, packetNumber, totalpackage, status, "接收方超时");
                return false;
            }
            finally
            {
                if (!transactionFinished)
                {
                    transaction.Finish();
                }
            }
        }

        public void StopTransmitting()
        {
            userCancel = true;
            isTramsitting = false;

        }
        /// <summary>
        /// 发送多个文件
        /// </summary>
        /// <param name="files"></param>
        public void YmodemSendFiles(List<string> files)
        {
            userCancel = false;
            for(int i= 0;i < files.Count;i++)
            {
                if (i != files.Count - 1)
                {
                    YmodemSendFile(files[i],false);
                }else
                {
                    YmodemSendFile(files[i]);
                }
                if (userCancel) break;
            }
        }
        private void sendYmodemInitialPacket(byte STX, int packetNumber, int invertedPacketNumber, byte[] data, int dataSize, string path, FileStream fileStream, byte[] CRC, int crcSize)
        {
            string fileName = System.IO.Path.GetFileName(path);
            // YModem协议不允许字符串中出现空格，将空格替换为下划线
            fileName = fileName.Replace(" ", "_");
            string fileSize = fileStream.Length.ToString();

            // 获取文件的最后修改时间
            DateTime lastWriteTime = File.GetLastWriteTime(path);

            // 手动计算Unix时间戳（从1970年1月1日到lastWriteTime的秒数）
            DateTimeOffset epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
            long unixTime = (lastWriteTime.ToUniversalTime().Ticks - epoch.Ticks) / TimeSpan.TicksPerSecond;

            // 将Unix时间戳转换为八进制字符串
            string fileModTime = Convert.ToString(unixTime, 8);


            // 将包数转换为八进制字符串
            string packageCount = Convert.ToString(totalpackage, 8);

            // 使用 Encoding 类中的 GetBytes 方法将字符串转换为 GB2312 编码的字节数组
            byte[] gb2312Bytes = Encoding.GetEncoding("gb2312").GetBytes(fileName);

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
                data[i  + j + 1] = (byte)fileSize.ToCharArray()[j];
            }
            data[i  + j + 1] = (byte)(' ');
            /* 将文件修改时间添加到数据中 */
            int m;
            for (m = 0; m < fileModTime.Length && (fileModTime.ToCharArray()[m] != 0); m++)
            {
                data[i + j  + m + 2] = (byte)fileModTime.ToCharArray()[m];
            }
            data[i + j  + m + 2] = (byte)(' ');
            /* 将文件修改时间添加到数据中 */
            int n;
            for (n = 0; n < packageCount.Length && (packageCount.ToCharArray()[n] != 0); n++)
            {
                data[i + j  + n + m + 3] = (byte)packageCount.ToCharArray()[n];
            }
            data[i + j + m + n + 3] = (byte)(' ');
            /* 用0填充剩余的数据字节 */
            for (int k = (i + j + m + n + 4); k < dataSize; k++)
            {
                data[k] = 0;
            }

            /* 计算CRC校验码 */
            Crc16Ccitt crc16Ccitt = new Crc16Ccitt(InitialCrcValue.Zeros);
            CRC = crc16Ccitt.ComputeChecksumBytes(data);

            /* 发送数据包 */
            sendYmodemPacket(STX, packetNumber, invertedPacketNumber, data, dataSize, CRC, crcSize);
        }

        private void sendYmodemClosingPacket(byte SOH, int packetNumber, int invertedPacketNumber, byte[] data, int dataSize, byte[] CRC, int crcSize)
        {
            /* 计算CRC校验码 */
            Crc16Ccitt crc16Ccitt = new Crc16Ccitt(InitialCrcValue.Zeros);
            CRC = crc16Ccitt.ComputeChecksumBytes(data);

            /* 发送数据包 */
            sendYmodemPacket(SOH, packetNumber, invertedPacketNumber, data, dataSize, CRC, crcSize);
        }

        private void sendYmodemPacket(byte STX, int packetNumber, int invertedPacketNumber, byte[] data, int dataSize, byte[] CRC, int crcSize)
        {
            int packetSize = 1 + 1 + 1 + dataSize + crcSize; // 计算包的总大小

            // 创建一个足够大的字节数组来存储整个包
            byte[] packet = new byte[packetSize];

            // 填充包数据
            packet[0] = STX;  // STX
            packet[1] = (byte)packetNumber;  // Packet Number
            packet[2] = (byte)invertedPacketNumber;  // Inverted Packet Number
            Array.Copy(data, 0, packet, 3, dataSize);  // 复制数据到包中
            Array.Copy(CRC, 0, packet, 3 + dataSize, crcSize);  // 复制CRC到包中

            // 通过串口一次性发送整个包
            serialPort.Write(packet, 0, packet.Length);

        }

    }
}
