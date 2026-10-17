namespace com.lifepixer.mangapixer.Server.Features.VolumeScan;

using System.Text.Json;
using System.Text.Json.Serialization;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>The stored state of a scanned volume file (<c>volume_contents.State</c>).</summary>
public enum VolumeContentRowState
{
    NoneFound = 0,
    Partial = 1,
    Known = 2,

    /// <summary>The volume or a candidate could not be hashed (an error code is stored); retried when a file changes.</summary>
    Failed = 3,

    /// <summary>Not matched (a folder over the matcher's pair guard); retried when the candidates change.</summary>
    Skipped = 4,
}

/// <summary>One chapter file proven inside a volume file, as stored in <c>volume_contents.ChaptersJson</c> (ids and numbers only).</summary>
public sealed record StoredFoundChapter(
    [property: JsonPropertyName("id")] long NodeId,
    [property: JsonPropertyName("cv")] long ContentVersion,
    [property: JsonPropertyName("n")] decimal? Number,
    [property: JsonPropertyName("f")] int FirstPage,
    [property: JsonPropertyName("l")] int LastPage,
    [property: JsonPropertyName("m")] int Matched,
    [property: JsonPropertyName("p")] int Pages,
    [property: JsonPropertyName("s")] bool Split = false);

/// <summary>
/// The row-reader seam of the volume contents scan (1.44.0): every reader of a folder's rows (the Volumes view, progress, Missing, the
/// export, duplicate counts) loads the stored answers for the archives it read and hands them to <see cref="VolumeContents.Apply"/>.
/// Only answers that still hold count: the volume file's content version must be the scanned one, and a chapter file whose content
/// changed since (or that is no longer among the rows) is dropped - a Known volume that lost a chapter reads Partial.
/// </summary>
public static class VolumeContentReader
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<StoredFoundChapter> ParseChapters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<StoredFoundChapter>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The scans of the volume files among <paramref name="archives"/> (internal node id -> the row id the caller uses), with the chapter
    /// rows among the same archives. Empty when nothing was scanned (one indexed query).
    /// </summary>
    public static async Task<IReadOnlyList<ScannedVolume>> LoadAsync(MangaPixerDbContext db, IReadOnlyDictionary<long, string> archives,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(archives);
        if (archives.Count == 0)
            return [];
        var ids = archives.Keys.ToList();
        var known = (int)VolumeContentRowState.Known;
        var partial = (int)VolumeContentRowState.Partial;
        var rows = new List<(long VolumeNodeId, long VolumeContentVersion, double Volume, int State, string ChaptersJson)>();
        foreach (var chunk in ids.Chunk(500))
        {
            var part = await db.VolumeContents.AsNoTracking()
                .Where(v => chunk.Contains(v.VolumeNodeId) && (v.State == known || v.State == partial))
                .Select(v => new { v.VolumeNodeId, v.VolumeContentVersion, v.Volume, v.State, v.ChaptersJson })
                .ToListAsync(ct);
            rows.AddRange(part.Select(p => (p.VolumeNodeId, p.VolumeContentVersion, p.Volume, p.State, p.ChaptersJson)));
        }
        if (rows.Count == 0)
            return [];

        var involved = rows.Select(r => r.VolumeNodeId)
            .Concat(rows.SelectMany(r => ParseChapters(r.ChaptersJson)).Select(c => c.NodeId))
            .Distinct()
            .ToList();
        var versions = new Dictionary<long, long>();
        foreach (var chunk in involved.Chunk(500))
        {
            foreach (var v in await db.ArchiveItems.AsNoTracking().Where(a => chunk.Contains(a.NodeId))
                         .Select(a => new { a.NodeId, a.ContentVersion }).ToListAsync(ct))
                versions[v.NodeId] = v.ContentVersion;
        }

        var result = new List<ScannedVolume>();
        foreach (var row in rows)
        {
            if (!archives.TryGetValue(row.VolumeNodeId, out var volumeRowId) || versions.GetValueOrDefault(row.VolumeNodeId, -1) != row.VolumeContentVersion)
                continue;
            var stored = ParseChapters(row.ChaptersJson);
            var chapters = stored
                .Where(c => archives.ContainsKey(c.NodeId) && versions.GetValueOrDefault(c.NodeId, -1) == c.ContentVersion)
                .Select(c => new ScannedChapter(archives[c.NodeId], c.Number))
                .ToList();
            if (chapters.Count == 0)
                continue;
            var state = row.State == known && chapters.Count == stored.Count ? VolumeContentState.Known : VolumeContentState.Partial;
            result.Add(new ScannedVolume(volumeRowId, (decimal)row.Volume, state, chapters));
        }
        return result;
    }

    private static long s_generation;

    /// <summary>
    /// Moves on whenever the scan stores an answer (this process): part of the readers' cache keys, so a cached Volumes view or progress
    /// is rebuilt with the new answer instead of waiting for its time-to-live.
    /// </summary>
    public static long Generation => Interlocked.Read(ref s_generation);

    internal static void Bump() => Interlocked.Increment(ref s_generation);
}

/// <summary>The stack line "Chapters 51-58 are inside the volume file" (1.44.0) as the stack DTOs carry it.</summary>
public static class VolumeContentLine
{
    public static Core.Api.InVolumeFileDto? Of(VolumeStack stack)
    {
        ArgumentNullException.ThrowIfNull(stack);
        return stack.InVolumeFile.Count == 0
            ? null
            : new Core.Api.InVolumeFileDto
            {
                From = VolumeGrouping.Canonical(stack.InVolumeFile[0]),
                To = VolumeGrouping.Canonical(stack.InVolumeFile[^1]),
                Count = stack.InVolumeFile.Count,
            };
    }
}

/// <summary>Counts of the stored answers (1.44.0) for the readers that only count.</summary>
public static class VolumeContentCounts
{
    /// <summary>
    /// The chapter files found inside a volume file, per scanned folder (of <paramref name="folderIds"/>), counting only answers whose
    /// volume file is unchanged since the scan.
    /// </summary>
    public static async Task<Dictionary<long, int>> ChaptersInsideAsync(MangaPixerDbContext db, IReadOnlyList<long> folderIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(folderIds);
        var result = new Dictionary<long, int>();
        if (folderIds.Count == 0)
            return result;
        var known = (int)VolumeContentRowState.Known;
        var partial = (int)VolumeContentRowState.Partial;
        var rows = await db.VolumeContents.AsNoTracking()
            .Where(v => folderIds.Contains(v.FolderId) && (v.State == known || v.State == partial))
            .Join(db.ArchiveItems, v => v.VolumeNodeId, a => a.NodeId, (v, a) => new { v.FolderId, v.ChaptersJson, Fresh = v.VolumeContentVersion == a.ContentVersion })
            .Where(x => x.Fresh)
            .ToListAsync(ct);
        foreach (var row in rows)
            result[row.FolderId] = result.GetValueOrDefault(row.FolderId) + VolumeContentReader.ParseChapters(row.ChaptersJson).Count;
        return result;
    }
}
