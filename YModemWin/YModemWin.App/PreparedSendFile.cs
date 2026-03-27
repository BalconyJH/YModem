namespace YModemWin;

public sealed record PreparedSendFile(
    string SourcePath,
    string DisplayFileName,
    DateTime LastWriteTime,
    byte[]? ParsedPayload,
    bool IsParsedPayload,
    string? ParserName,
    int? ParsedSegmentCount)
{
    public static PreparedSendFile FromRawFile(string path) =>
        new(path, Path.GetFileName(path), File.GetLastWriteTime(path), null, false, null, null);

    public static PreparedSendFile FromParsedData(
        string sourcePath,
        byte[] payload,
        string parserName,
        int segmentCount,
        ulong firstSegmentStartAddress)
    {
        var originalFileName = Path.GetFileName(sourcePath);
        var binFileName = Path.ChangeExtension(originalFileName, ".bin");
        var displayName = $"[0x{firstSegmentStartAddress:X8}]{binFileName}";
        return new PreparedSendFile(
            sourcePath,
            displayName,
            File.GetLastWriteTime(sourcePath),
            payload,
            true,
            parserName,
            segmentCount);
    }

    public override string ToString()
    {
        var kind = IsParsedPayload ? "PARSED" : "RAW";
        return $"[{kind}] {DisplayFileName}";
    }
}
