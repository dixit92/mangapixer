namespace com.lifepixer.mangapixer.Server.Operations.Perf;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>
/// Measurement-only request and SQL timing (1.46.0 lane M). OFF by default; on only with
/// <c>MangaPixer:Perf:RequestTiming=true</c> on an instance someone measures. When on, every <c>/api</c> request gets a
/// <c>Server-Timing</c> header (<c>app</c>, <c>sql</c>, <c>sqln</c>, <c>lockw</c> - durations and counts only) and the admin
/// diagnostics snapshot gets a <c>perf</c> block: per route template, per SQL statement shape (string literals and numbers
/// replaced, so no value from the data can appear), transaction begin / commit waits (SQLite's write lock), the GC heap and
/// the thread pool. Nothing is logged, nothing leaves the process.
/// </summary>
public sealed class PerfRecorder : IDisposable
{
    public const string ConfigKey = "MangaPixer:Perf:RequestTiming";

    private static PerfRecorder? s_active;
    private static readonly AsyncLocal<RequestPerfScope?> s_scope = new();

    /// <summary>The recorder of the host that switched timing on (null when no host did). Read by the EF interceptors.</summary>
    public static PerfRecorder? Active => Volatile.Read(ref s_active);

    internal static RequestPerfScope? CurrentScope => s_scope.Value;

    private const int MaxKeys = 4000;
    internal static readonly double[] BucketUpperMs = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 30000, double.PositiveInfinity];

    private readonly ConcurrentDictionary<string, Agg> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Origin, string Shape), Agg> _statements = new();
    private readonly ConcurrentDictionary<string, Agg> _transactions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _shapeCache = new(StringComparer.Ordinal);
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    public const string TrackerMarksKey = "MangaPixer:Perf:TrackerMarksWithNetworkOff";

    public bool Enabled { get; }

    /// <summary>Measurement only: card marks count every known tracker as allowed although the tracker network is off.</summary>
    public bool TrackerMarksWithNetworkOff { get; }

    public PerfRecorder(bool enabled, bool trackerMarksWithNetworkOff = false)
    {
        Enabled = enabled;
        TrackerMarksWithNetworkOff = enabled && trackerMarksWithNetworkOff;
        if (enabled)
            Volatile.Write(ref s_active, this);
    }

    /// <summary>The host stopped: its recorder stops collecting (other hosts in the same process, e.g. tests, record nothing).</summary>
    public void Dispose() => Interlocked.CompareExchange(ref s_active, null, this);

    /// <summary>Starts the request scope the SQL interceptors add to (the caller restores the previous one).</summary>
    internal RequestPerfScope BeginRequest()
    {
        var scope = new RequestPerfScope();
        s_scope.Value = scope;
        return scope;
    }

    internal void EndRequest(RequestPerfScope scope, string route, double totalMs)
    {
        s_scope.Value = null;
        scope.End();
        // Statements first: a reader that sees the route counted also sees its statements (the response may reach the client
        // before this runs).
        foreach (var (shape, s) in scope.Statements)
            BumpStatement((route, shape), s.Ms, s.Count, s.Rows);
        Bump(_routes, route, totalMs, scope.SqlMs, scope.SqlCount, scope.LockWaitMs);
    }

    /// <summary>One executed SQL command: inside a request it goes to that request's scope, otherwise to "background".</summary>
    internal void RecordCommand(string commandText, double ms, int rows, bool countAsCommand = true)
    {
        var shape = ShapeOf(commandText);
        var scope = CurrentScope;
        if (scope is { Ended: false })
        {
            scope.Add(shape, ms, rows, countAsCommand);
            return;
        }
        // Work a request started but did not wait for (its async flow outlived the response) counts apart from background.
        BumpStatement((scope is null ? "(background)" : "(after a request)", shape), ms, countAsCommand ? 1 : 0, rows);
    }

    /// <summary>BEGIN (SQLite takes the write lock for BEGIN IMMEDIATE) or COMMIT duration.</summary>
    internal void RecordTransaction(string phase, double ms)
    {
        var scope = CurrentScope is { Ended: false } live ? live : null;
        var origin = scope is null ? "background" : "request";
        if (scope is not null && phase == "begin")
            scope.AddLockWait(ms);
        Bump(_transactions, $"{origin}:{phase}", ms, 0, 0, 0);
    }

    private void BumpStatement((string, string) key, double ms, int count, long rows)
    {
        if (!_statements.ContainsKey(key) && _statements.Count >= MaxKeys)
            key = (key.Item1, "(other statements)");
        var agg = _statements.GetOrAdd(key, _ => new Agg());
        agg.Add(ms, count, rows);
    }

    private static void Bump(ConcurrentDictionary<string, Agg> map, string key, double ms, double sqlMs, int sqlCount, double lockMs)
    {
        if (!map.ContainsKey(key) && map.Count >= MaxKeys)
            key = "(other)";
        map.GetOrAdd(key, _ => new Agg()).Add(ms, 1, 0, sqlMs, sqlCount, lockMs);
    }

    private static readonly Regex Literal = new(@"'(?:[^']|'')*'", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Number = new(@"(?<![A-Za-z_@$])\d+(?:\.\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The statement's shape: whitespace collapsed, every string literal -> <c>'?'</c>, every number -> <c>#</c>. EF sends values
    /// as parameters, so the text is the query the code built; the replacement keeps any inlined value out even so.
    /// </summary>
    internal string ShapeOf(string commandText)
    {
        if (_shapeCache.TryGetValue(commandText, out var cached))
            return cached;
        var shape = Space.Replace(Number.Replace(Literal.Replace(commandText, "'?'"), "#"), " ").Trim();
        if (_shapeCache.Count < MaxKeys)
            _shapeCache[commandText] = shape;
        return shape;
    }

    public PerfDiagnostics Snapshot(int maxStatements = 1500)
    {
        var gc = GC.GetGCMemoryInfo();
        ThreadPool.GetAvailableThreads(out var workerAvail, out var ioAvail);
        ThreadPool.GetMaxThreads(out var workerMax, out var ioMax);
        return new PerfDiagnostics
        {
            UptimeSeconds = Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds,
            BucketUpperMs = BucketUpperMs.Select(b => double.IsInfinity(b) ? -1 : b).ToArray(),
            Routes = _routes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value.ToRow(kv.Key, null)).ToArray(),
            Statements = _statements
                .OrderByDescending(kv => kv.Value.MsSum)
                .Take(maxStatements)
                .Select(kv => kv.Value.ToRow(kv.Key.Origin, kv.Key.Shape))
                .ToArray(),
            Transactions = _transactions.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value.ToRow(kv.Key, null)).ToArray(),
            Heap = new PerfHeap
            {
                TotalMemoryBytes = GC.GetTotalMemory(false),
                HeapSizeBytes = gc.HeapSizeBytes,
                CommittedBytes = gc.TotalCommittedBytes,
                FragmentedBytes = gc.FragmentedBytes,
                TotalAllocatedBytes = GC.GetTotalAllocatedBytes(false),
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                PauseTimePercentage = gc.PauseTimePercentage,
                TotalPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds,
                WorkingSetBytes = Environment.WorkingSet,
            },
            ThreadPool = new PerfThreadPool
            {
                Threads = ThreadPool.ThreadCount,
                PendingWorkItems = ThreadPool.PendingWorkItemCount,
                CompletedWorkItems = ThreadPool.CompletedWorkItemCount,
                BusyWorkerThreads = workerMax - workerAvail,
                BusyIoThreads = ioMax - ioAvail,
            },
        };
    }

    private sealed class Agg
    {
        private readonly object _gate = new();
        private long _count, _rows, _sqlCount;
        private double _msSum, _msMax, _sqlMsSum, _lockMsSum;
        private readonly long[] _buckets = new long[BucketUpperMs.Length];

        public double MsSum { get { lock (_gate) return _msSum; } }

        public void Add(double ms, int count, long rows, double sqlMs = 0, int sqlCount = 0, double lockMs = 0)
        {
            lock (_gate)
            {
                _count += count;
                _rows += rows;
                _msSum += ms;
                _sqlMsSum += sqlMs;
                _sqlCount += sqlCount;
                _lockMsSum += lockMs;
                if (ms > _msMax) _msMax = ms;
                // One bucket entry per call: for a route that is one request; for a statement merged from one request it is
                // the request's summed time of that statement.
                var i = 0;
                while (ms > BucketUpperMs[i]) i++;
                _buckets[i]++;
            }
        }

        public PerfRow ToRow(string key, string? shape)
        {
            lock (_gate)
            {
                return new PerfRow
                {
                    Key = key,
                    Shape = shape,
                    Count = _count,
                    MsSum = Math.Round(_msSum, 1),
                    MsMax = Math.Round(_msMax, 1),
                    Rows = _rows,
                    SqlMsSum = Math.Round(_sqlMsSum, 1),
                    SqlCount = _sqlCount,
                    LockWaitMsSum = Math.Round(_lockMsSum, 1),
                    Buckets = (long[])_buckets.Clone(),
                };
            }
        }
    }
}

/// <summary>
/// Measurement-only phase timer (1.46.0 lane M): <c>var t = PerfPhase.Start(); ...; t = t.Lap("browse.x");</c> records the time since
/// the last lap as <c>phase:browse.x</c> in the current request (shown with the route's statements in the diagnostics). A no-op
/// outside a timed request.
/// </summary>
public readonly struct PerfPhase
{
    private readonly long _start;

    private PerfPhase(long start) => _start = start;

    public static PerfPhase Start() => PerfRecorder.CurrentScope is { Ended: false } ? new PerfPhase(Stopwatch.GetTimestamp()) : default;

    public PerfPhase Lap(string name)
    {
        if (_start == 0 || PerfRecorder.CurrentScope is not { Ended: false } scope)
            return default;
        var now = Stopwatch.GetTimestamp();
        scope.AddPhase(name, Stopwatch.GetElapsedTime(_start, now).TotalMilliseconds);
        return new PerfPhase(now);
    }
}

/// <summary>What one request did in SQL (filled by the interceptors on the request's async flow).</summary>
internal sealed class RequestPerfScope
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (double Ms, int Count, long Rows)> _statements = new(StringComparer.Ordinal);

    private volatile bool _ended;

    public bool Ended => _ended;

    public void End() => _ended = true;

    public double SqlMs { get; private set; }
    public int SqlCount { get; private set; }
    public double LockWaitMs { get; private set; }

    public IEnumerable<KeyValuePair<string, (double Ms, int Count, long Rows)>> Statements
    {
        get { lock (_gate) return _statements.ToArray(); }
    }

    public void Add(string shape, double ms, int rows, bool countAsCommand = true)
    {
        var n = countAsCommand ? 1 : 0;
        lock (_gate)
        {
            SqlMs += ms;
            SqlCount += n;
            _statements[shape] = _statements.TryGetValue(shape, out var s) ? (s.Ms + ms, s.Count + n, s.Rows + rows) : (ms, n, rows);
        }
    }

    public void AddPhase(string name, double ms)
    {
        var key = "phase:" + name;
        lock (_gate)
            _statements[key] = _statements.TryGetValue(key, out var s) ? (s.Ms + ms, s.Count + 1, s.Rows) : (ms, 1, 0);
    }

    public void AddLockWait(double ms)
    {
        lock (_gate) LockWaitMs += ms;
    }
}

/// <summary>The <c>perf</c> block of the admin diagnostics snapshot (measurement only; counts and durations).</summary>
public sealed record PerfDiagnostics
{
    public required double UptimeSeconds { get; init; }
    public required double[] BucketUpperMs { get; init; }
    public required PerfRow[] Routes { get; init; }
    public required PerfRow[] Statements { get; init; }
    public required PerfRow[] Transactions { get; init; }
    public required PerfHeap Heap { get; init; }
    public required PerfThreadPool ThreadPool { get; init; }
}

public sealed record PerfRow
{
    /// <summary>Route template (<c>GET api/v1/...{id}</c>), <c>(background)</c>, or a transaction phase.</summary>
    public required string Key { get; init; }

    /// <summary>The SQL statement's shape (statements only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Shape { get; init; }

    public required long Count { get; init; }
    public required double MsSum { get; init; }
    public required double MsMax { get; init; }
    public required long Rows { get; init; }
    public required double SqlMsSum { get; init; }
    public required long SqlCount { get; init; }
    public required double LockWaitMsSum { get; init; }
    public required long[] Buckets { get; init; }
}

public sealed record PerfHeap
{
    public required long TotalMemoryBytes { get; init; }
    public required long HeapSizeBytes { get; init; }
    public required long CommittedBytes { get; init; }
    public required long FragmentedBytes { get; init; }
    public required long TotalAllocatedBytes { get; init; }
    public required int Gen0Collections { get; init; }
    public required int Gen1Collections { get; init; }
    public required int Gen2Collections { get; init; }
    public required double PauseTimePercentage { get; init; }
    public required double TotalPauseMs { get; init; }
    public required long WorkingSetBytes { get; init; }
}

public sealed record PerfThreadPool
{
    public required int Threads { get; init; }
    public required long PendingWorkItems { get; init; }
    public required long CompletedWorkItems { get; init; }
    public required int BusyWorkerThreads { get; init; }
    public required int BusyIoThreads { get; init; }
}
