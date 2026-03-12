namespace YModemWin;

public sealed record PreparedSendFile(
    string SourcePath,
    string DisplayFileName,
    DateTime LastWriteTime,
    byte[]? ParsedPayload,
    bool IsParsedPayload)
{
    public static PreparedSendFile FromRawFile(string path) =>
        new(path, Path.GetFileName(path), File.GetLastWriteTime(path), null, false);

    public static PreparedSendFile FromParsedData(string sourcePath, byte[] payload)
    {
        var originalFileName = Path.GetFileName(sourcePath);
        var binFileName = Path.ChangeExtension(originalFileName, ".bin");
        return new PreparedSendFile(sourcePath, binFileName, File.GetLastWriteTime(sourcePath), payload, true);
    }

    public override string ToString()
    {
        var kind = IsParsedPayload ? "PARSED" : "RAW";
        return $"[{kind}] {DisplayFileName}";
    }
}
