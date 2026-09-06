namespace com.lifepixer.mangaplex.MediaWorker;

/// <summary>
/// Minimal Program.cs for P00: health-only worker startup.
/// Full archive/image processing is added in P01/P07.
/// The worker communicates via stdin/stdout JSON-lines, not a network listener.
/// </summary>
public sealed class Program
{
    public static void Main(string[] args)
    {
        // P00: minimal stub. Real worker protocol is implemented in P07.
        Console.Error.WriteLine("MangaPlex MediaWorker started (P00 stub)");
        Console.Out.WriteLine("""{"event":"worker_started","status":"ready"}""");
    }
}
