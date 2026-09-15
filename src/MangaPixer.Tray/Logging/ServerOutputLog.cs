namespace com.lifepixer.mangapixer.Tray.Logging;

/// <summary>
/// Persists lines drained from the server child process's redirected
/// stdout/stderr, size-capped with a single rollover file. Server output is
/// already privacy-sanitized per the repo's logging invariants (Serilog's
/// console sink never writes paths/credentials), so keeping it on disk under
/// the tray's own data directory helps diagnose a server that won't start.
/// </summary>
public sealed class ServerOutputLog : IDisposable
{
    public const long DefaultMaxSizeBytes = 5 * 1024 * 1024;

    private readonly string _filePath;
    private readonly long _maxSizeBytes;
    private readonly object _gate = new();
    private StreamWriter _writer;

    public ServerOutputLog(string filePath, long maxSizeBytes = DefaultMaxSizeBytes)
    {
        _filePath = filePath;
        _maxSizeBytes = maxSizeBytes;

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _writer = new StreamWriter(filePath, append: true) { AutoFlush = false };
    }

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MangaPlex", "logs", "server-output.log");

    public void WriteLine(string line)
    {
        lock (_gate)
        {
            RollIfOverCap();
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    private void RollIfOverCap()
    {
        _writer.Flush();
        if (new FileInfo(_filePath).Length < _maxSizeBytes)
            return;

        _writer.Dispose();
        var rolledPath = _filePath + ".1";
        if (File.Exists(rolledPath))
            File.Delete(rolledPath);
        File.Move(_filePath, rolledPath);
        _writer = new StreamWriter(_filePath, append: true) { AutoFlush = false };
    }

    public void Dispose()
    {
        lock (_gate)
            _writer.Dispose();
    }
}
