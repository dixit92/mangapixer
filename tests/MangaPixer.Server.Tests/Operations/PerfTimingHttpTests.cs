namespace com.lifepixer.mangapixer.Tests.Server.Operations;

using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using com.lifepixer.mangapixer.Server.Operations.Perf;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Measurement-only request / SQL timing (1.46.0 lane M) through the public surface: off by default (no header, no
/// <c>perf</c> block); with <c>MangaPixer:Perf:RequestTiming=true</c> an API response carries the <c>Server-Timing</c>
/// fields and the admin diagnostics snapshot lists the route, the SQL statement shapes and the heap.
/// </summary>
public sealed class PerfTimingHttpTests
{
    private const string Diagnostics = "/api/v1/operations/diagnostics";

    [Fact]
    public async Task Default_AddsNoTimingHeader_AndNoPerfBlock()
    {
        using var factory = new MangaPixerWebApplicationFactory();
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.GetAsync(Diagnostics);
        response.EnsureSuccessStatusCode();
        Assert.False(response.Headers.Contains("Server-Timing"));
        var diag = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(diag.TryGetProperty("perf", out _));
    }

    [Fact]
    public async Task Switched_On_TimesRequests_AndListsRoutesStatementsAndHeap()
    {
        var factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(
            new Dictionary<string, string?> { [PerfRecorder.ConfigKey] = "true" });
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();
        var recorder = factory.Services.GetRequiredService<PerfRecorder>();

        var first = await client.GetAsync(Diagnostics);
        await factory.UnhandledErrors.EnsureSuccessAsync(first); // a 500 names its exception (1.47.0 lane T2)
        var timing = string.Join(", ", first.Headers.GetValues("Server-Timing"));
        // Durations and counts only: every part is a known field with a number.
        Assert.All(timing.Split(", "), part => Assert.Matches(@"^(app|sql|sqln|lockw);dur=[0-9]+(\.[0-9])?$", part));
        Assert.Matches(@"(^|, )sqln;dur=[1-9][0-9]*", timing); // the cookie check and the snapshot's counts are SQL

        // The first request is counted when its middleware finishes, which may be just after the client got the response.
        JsonElement perf = default;
        List<JsonElement> routes = [];
        for (var attempt = 0; attempt < 50 && !routes.Any(r => r.GetProperty("key").GetString() == "GET api/v1/operations/diagnostics"); attempt++)
        {
            if (attempt > 0)
                await Task.Delay(100);
            var diag = await client.GetFromJsonAsync<JsonElement>(Diagnostics);
            perf = diag.GetProperty("perf");
            routes = perf.GetProperty("routes").EnumerateArray().ToList();
        }
        var route = Assert.Single(routes, r => r.GetProperty("key").GetString() == "GET api/v1/operations/diagnostics");
        Assert.True(route.GetProperty("count").GetInt64() >= 1);
        Assert.True(route.GetProperty("sqlCount").GetInt64() >= 1);
        var statements = perf.GetProperty("statements").EnumerateArray()
            .Where(s => s.GetProperty("key").GetString() == "GET api/v1/operations/diagnostics").ToList();
        var shapes = string.Join(" | ", statements.Select(s => s.GetProperty("shape").GetString()![..Math.Min(40, s.GetProperty("shape").GetString()!.Length)]));
        Assert.True(statements.Any(s => s.GetProperty("shape").GetString()!.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && s.GetProperty("count").GetInt64() >= 1), shapes);
        Assert.True(perf.GetProperty("heap").GetProperty("heapSizeBytes").GetInt64() > 0);
        Assert.Contains(perf.GetProperty("transactions").EnumerateArray(), t => t.GetProperty("key").GetString()!.EndsWith(":commit", StringComparison.Ordinal));

        // The process-wide hook ends with the host: later hosts in the same process (other tests) record nothing into it.
        Assert.Same(recorder, PerfRecorder.Active);
        factory.Dispose();
        Assert.NotSame(recorder, PerfRecorder.Active);
    }

    /// <summary>
    /// 1.47.0 lane T2 - the cause of the 500 this class's switched-on test got on its first diagnostics GET in a full Server run (alone
    /// it passed): the SQL hook is process-wide, so while the measured host takes its snapshot, the SQL of every other host in the
    /// process (the other tests) keeps adding new statement shapes. A LINQ sort straight over a <c>ConcurrentDictionary</c> copies
    /// it by <c>Count</c> + <c>CopyTo</c>, and <c>CopyTo</c> throws <see cref="ArgumentException"/> when a key arrived in between.
    /// The snapshot must copy each map atomically. Writers on their own threads add new shapes and routes the whole time; a fresh
    /// recorder replaces a full one, since past 4000 keys new shapes fold into one row and stop adding keys.
    /// </summary>
    [Fact]
    public void Snapshot_WhileNewStatementsAndRoutesArrive_NeverThrows()
    {
        var current = new StrongBox<PerfRecorder>(new PerfRecorder(enabled: false));
        var stop = 0;
        var writers = new[]
        {
            new Thread(() =>
            {
                for (var i = 0; Volatile.Read(ref stop) == 0; i++)
                    Volatile.Read(ref current.Value)!.RecordCommand($"SELECT c{i}x FROM t", 0.5, 1);
            }),
            new Thread(() =>
            {
                for (var i = 0; Volatile.Read(ref stop) == 0; i++)
                {
                    var recorder = Volatile.Read(ref current.Value)!;
                    recorder.EndRequest(recorder.BeginRequest(), $"GET api/v1/r{i}x", 1);
                }
            }),
        };
        foreach (var writer in writers)
            writer.Start();
        try
        {
            var snapshots = 0;
            for (var watch = Stopwatch.StartNew(); watch.Elapsed < TimeSpan.FromSeconds(2); snapshots++)
            {
                var snapshot = Volatile.Read(ref current.Value)!.Snapshot();
                if (snapshot.Statements.Length >= 1500 || snapshot.Routes.Length >= 3000)
                    Volatile.Write(ref current.Value, new PerfRecorder(enabled: false));
            }
            Assert.True(snapshots > 10, $"only {snapshots} snapshots");
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            foreach (var writer in writers)
                writer.Join();
        }
    }

    [Fact]
    public void StatementShape_KeepsNoValue()
    {
        var recorder = new PerfRecorder(enabled: false);
        var shape = recorder.ShapeOf("SELECT \"c\".\"Id1\", @__p_0 FROM t0\n  WHERE name = 'A Secret ''Title''' AND n = 42 AND x > 3.5");
        Assert.Equal("SELECT \"c\".\"Id1\", @__p_0 FROM t0 WHERE name = '?' AND n = # AND x > #", shape);
    }

    [Fact]
    public void PhaseLaps_AreCountedWithTheRoute_AndAreNoOpsOutsideARequest()
    {
        PerfPhase.Start().Lap("outside"); // no request scope: nothing happens

        var recorder = new PerfRecorder(enabled: false);
        var scope = recorder.BeginRequest();
        var phase = PerfPhase.Start();
        phase = phase.Lap("first");
        phase.Lap("second");
        recorder.EndRequest(scope, "GET api/v1/x", 1);

        var rows = recorder.Snapshot().Statements.Where(s => s.Key == "GET api/v1/x").ToList();
        Assert.Equal(["phase:first", "phase:second"], rows.Select(r => r.Shape).Order(StringComparer.Ordinal));
        Assert.All(rows, r => Assert.Equal(1L, r.Count));
        Assert.DoesNotContain(recorder.Snapshot().Statements, s => s.Shape == "phase:outside");
    }
}
