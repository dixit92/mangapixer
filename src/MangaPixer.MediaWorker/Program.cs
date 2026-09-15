namespace com.lifepixer.mangapixer.MediaWorker;

using System.Threading;

/// <summary>
/// MediaWorker entry point. Communicates via stdin/stdout JSON-lines protocol.
/// No network listener. No database access. Opens source archives read-only.
/// </summary>
public sealed class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();

        // Handle Ctrl+C / SIGTERM for graceful shutdown
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // The worker reads protocol messages from stdin and writes to stdout.
        // stderr is for diagnostics only (drained and sanitized by the server).
        var loop = new WorkerLoop(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            Console.Error,
            cts.Token);

        try
        {
            await loop.RunAsync();
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Worker fatal error: {ex.GetType().Name}");
            return 1;
        }
    }
}
