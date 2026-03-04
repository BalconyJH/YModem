namespace YModemWin.Core
{
    public enum InitialCrcValue : ushort
    {
        Zeros = 0x0000,
        NonZero1 = 0xFFFF,
        NonZero2 = 0x1D0F,
    }

    public sealed class Crc16Ccitt
    {
        private const ushort Poly = 0x1021;
        private static readonly ushort[] Table = BuildTable();

        private readonly ushort _initialValue;

        public Crc16Ccitt(InitialCrcValue initialValue = InitialCrcValue.Zeros)
        {
            _initialValue = (ushort)initialValue;
        }

        public ushort ComputeChecksum(byte[] bytes) => Compute(bytes);

        public ushort Compute(ReadOnlySpan<byte> data)
        {
            var crc = _initialValue;

            foreach (var b in data)
            {
                var idx = (byte)((crc >> 8) ^ b);
                crc = (ushort)((crc << 8) ^ Table[idx]);
            }

            return crc;
        }

        public byte[] ComputeChecksumBytes(byte[] bytes)
        {
            var crc = Compute(bytes);
            return new[] { (byte)(crc >> 8), (byte)crc };
        }

        public void WriteChecksumBigEndian(ReadOnlySpan<byte> data, Span<byte> destination)
        {
            var crc = Compute(data);
            destination[0] = (byte)(crc >> 8);
            destination[1] = (byte)crc;
        }

        private static ushort[] BuildTable()
        {
            var table = new ushort[256];

            for (var i = 0; i < table.Length; i++)
            {
                ushort crc = 0;
                ushort v = (ushort)(i << 8);

                for (var j = 0; j < 8; j++)
                {
                    crc = (ushort)(((crc ^ v) & 0x8000) != 0 ? (crc << 1) ^ Poly : crc << 1);
                    v <<= 1;
                }

                table[i] = crc;
            }

            return table;
        }
    }
}
