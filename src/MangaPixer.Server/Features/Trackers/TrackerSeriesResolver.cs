namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using System.Globalization;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Declared;
using com.lifepixer.mangapixer.Server.Features.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>A tracker id of a series and where it came from.</summary>
public sealed record TrackerRemoteId(string Id, TrackerIdSource Source);

/// <summary>One user's progress of one series record: the numbers and the day of the earliest counted mark.</summary>
public sealed record TrackerSeriesProgress(TrackerProgressResult Numbers, DateOnly? StartedOn, string? NodePublicId)
{
    public static readonly TrackerSeriesProgress Empty = new(new TrackerProgressResult(TrackerNumbers.None, TrackerNumbers.None), null, null);
}

/// <summary>
/// Which series a read mark belongs to and whether it may sync (1.43.0): the archive's nearest Confirmed / Auto link (as
/// <c>CoverLinks.NearestAsync</c>: Don't match, "Collection about", Artist folder and Needs review never sync); an archive in one of the
/// user's Private libraries, or a doujinshi record, does not sync unless the user turned sync ON on a folder or series above it (owner
/// Q3 / Q11); a user's sync OFF switch stops it. The nearest switch wins. Incognito (a request header) plays no part.
/// Also the tracker ids (never a title search, owner Q11) and the user's progress over every folder of the record they can open.
/// </summary>
public sealed class TrackerSeriesResolver(MangaPixerDbContext db, LibraryAuthorizationService libraries)
{
    private const int Batch = 500;

    /// <summary>The series record of each archive that may sync for <paramref name="userId"/>; archives that may not are left out.</summary>
    public async Task<Dictionary<long, long>> RecordsOfAsync(long userId, IReadOnlyCollection<long> itemIds, CancellationToken ct)
    {
        var result = new Dictionary<long, long>();
        foreach (var chunk in itemIds.Distinct().Chunk(Batch))
        {
            var links = await CoverLinks.NearestAsync(db, chunk, ct);
            var linked = links.Where(l => l.Value.IsLinked).ToDictionary(l => l.Key, l => l.Value.RecordId!.Value);
            if (linked.Count == 0)
                continue;
            var eligible = await EligibleAsync(userId, linked, ct);
            foreach (var (item, record) in linked)
            {
                if (eligible.Contains(item))
                    result[item] = record;
            }
        }
        return result;
    }

    /// <summary>
    /// The archives (of <paramref name="itemRecords"/>: archive -> record) that may sync: the user can open the library, and the
    /// nearest sync switch is on - or there is none and the archive is neither in a Private library of the user nor of a doujinshi record.
    /// </summary>
    private async Task<HashSet<long>> EligibleAsync(long userId, IReadOnlyDictionary<long, long> itemRecords, CancellationToken ct)
    {
        var ids = itemRecords.Keys.ToList();
        var nodeLibraries = await db.CatalogNodes.AsNoTracking().Where(n => ids.Contains(n.Id))
            .Select(n => new { n.Id, n.LibraryId }).ToDictionaryAsync(n => n.Id, n => n.LibraryId, ct);
        var accessible = (await libraries.GetAccessibleLibraryIdsAsync(userId, ct)).ToHashSet();
        var privateLibraries = (await db.PrivateLibraries.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.LibraryId).ToListAsync(ct))
            .ToHashSet();
        var recordIds = itemRecords.Values.Distinct().ToList();
        var doujinshi = (await db.MetadataRecords.AsNoTracking()
                .Where(r => recordIds.Contains(r.Id) && r.Format == (int)MetadataFormat.Doujinshi).Select(r => r.Id).ToListAsync(ct))
            .ToHashSet();
        var switches = await NearestSwitchAsync(userId, ids, ct);

        var result = new HashSet<long>();
        foreach (var (item, record) in itemRecords)
        {
            if (!nodeLibraries.TryGetValue(item, out var library) || !accessible.Contains(library))
                continue;
            var on = switches.TryGetValue(item, out var s) ? s : (bool?)null;
            if (on == false)
                continue;
            if (on is null && (privateLibraries.Contains(library) || doujinshi.Contains(record)))
                continue;
            result.Add(item);
        }
        return result;
    }

    /// <summary>
    /// 1.44.0: why reading below <paramref name="nodeId"/> does not sync for <paramref name="userId"/> - the rule of <see cref="EligibleAsync"/>
    /// for that folder: its nearest switch off; or no switch and a Private library of the user, or a doujinshi record - else null.
    /// </summary>
    public async Task<TrackerNotSynced?> NotSyncedAsync(long userId, long nodeId, long recordId, CancellationToken ct)
    {
        if ((await NearestSwitchAsync(userId, [nodeId], ct)).TryGetValue(nodeId, out var on))
            return on ? null : TrackerNotSynced.SwitchedOff;
        var library = await db.CatalogNodes.AsNoTracking().Where(n => n.Id == nodeId).Select(n => n.LibraryId).FirstAsync(ct);
        if (await db.PrivateLibraries.AsNoTracking().AnyAsync(p => p.UserId == userId && p.LibraryId == library, ct))
            return TrackerNotSynced.PrivateLibrary;
        if (await db.MetadataRecords.AsNoTracking().AnyAsync(r => r.Id == recordId && r.Format == (int)MetadataFormat.Doujinshi, ct))
            return TrackerNotSynced.Doujinshi;
        return null;
    }

    /// <summary>The user's nearest sync switch (self -> ancestors, bounded 64) of each node, when one exists.</summary>
    public async Task<Dictionary<long, bool>> NearestSwitchAsync(long userId, IReadOnlyCollection<long> nodeIds, CancellationToken ct)
    {
        var result = new Dictionary<long, bool>();
        if (nodeIds.Count == 0 || !await db.TrackerNodeOverrides.AnyAsync(o => o.UserId == userId, ct))
            return result;
        foreach (var chunk in nodeIds.Distinct().Chunk(Batch))
        {
            var ids = string.Join(",", chunk.Select(i => i.ToString(CultureInfo.InvariantCulture)));
            var connection = db.Database.GetDbConnection();
            var wasOpen = connection.State == System.Data.ConnectionState.Open;
            if (!wasOpen) await connection.OpenAsync(ct);
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    WITH RECURSIVE up(StartId, NodeId, ParentId, Depth) AS (
                        SELECT cn.Id, cn.Id, cn.ParentId, 0 FROM catalog_nodes cn WHERE cn.Id IN ({ids})
                        UNION ALL
                        SELECT u.StartId, p.Id, p.ParentId, u.Depth + 1
                        FROM up u JOIN catalog_nodes p ON p.Id = u.ParentId
                        WHERE u.Depth < 64
                    ),
                    hits AS (
                        SELECT u.StartId, o.Enabled, ROW_NUMBER() OVER (PARTITION BY u.StartId ORDER BY u.Depth) AS rn
                        FROM up u JOIN tracker_node_overrides o ON o.NodeId = u.NodeId AND o.UserId = $user
                    )
                    SELECT StartId, Enabled FROM hits WHERE rn = 1;
                    """;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "$user";
                parameter.Value = userId;
                command.Parameters.Add(parameter);
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    result[reader.GetInt64(0)] = reader.GetInt64(1) != 0;
            }
            finally
            {
                if (!wasOpen) await connection.CloseAsync();
            }
        }
        return result;
    }

    /// <summary>
    /// The tracker's id of a series record: an admin's id first (Q6); MangaUpdates = the linked MangaUpdates record's own id; MyAnimeList
    /// = <c>CrossIdsJson["mal"]</c> of the series' MangaDex record (the record itself when linked to MangaDex, else its found companion).
    /// Null when none is known - never a title search.
    /// </summary>
    public async Task<TrackerRemoteId?> RemoteIdAsync(long recordId, string tracker, CancellationToken ct)
    {
        var overrideId = await db.TrackerIdOverrides.AsNoTracking()
            .Where(o => o.RecordId == recordId && o.Tracker == tracker).Select(o => o.RemoteId).FirstOrDefaultAsync(ct);
        if (TrackerFacts.IsValidRemoteId(overrideId))
            return new TrackerRemoteId(overrideId!, TrackerIdSource.Admin);
        return await StoredRemoteIdAsync(recordId, tracker, ct);
    }

    /// <summary>The id from stored records only (no admin override).</summary>
    public async Task<TrackerRemoteId?> StoredRemoteIdAsync(long recordId, string tracker, CancellationToken ct)
    {
        var record = await db.MetadataRecords.AsNoTracking().Where(r => r.Id == recordId)
            .Select(r => new { r.Provider, r.ExternalId, r.CrossIdsJson }).FirstOrDefaultAsync(ct);
        if (record is null)
            return null;
        switch (tracker)
        {
            case TrackerSlugs.MangaUpdates:
                return record.Provider == MetadataProviderAllowlist.MangaUpdates && TrackerFacts.IsValidRemoteId(record.ExternalId)
                    ? new TrackerRemoteId(record.ExternalId, TrackerIdSource.Link)
                    : null;
            case TrackerSlugs.MyAnimeList:
                string? crossIds;
                if (record.Provider == MetadataProviderAllowlist.MangaDex)
                {
                    crossIds = record.CrossIdsJson;
                }
                else
                {
                    var found = new[] { (int)CompanionState.Auto, (int)CompanionState.Confirmed };
                    crossIds = await (from c in db.MetadataCompanions.AsNoTracking()
                                      join r in db.MetadataRecords.AsNoTracking() on c.CompanionRecordId equals r.Id
                                      where c.RecordId == recordId && c.Provider == MetadataProviderAllowlist.MangaDex && found.Contains(c.State)
                                      select r.CrossIdsJson).FirstOrDefaultAsync(ct);
                }
                return CrossId(crossIds, TrackerCrossIdKeys.MyAnimeList) is { } mal ? new TrackerRemoteId(mal, TrackerIdSource.MangaDex) : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// 1.43.0 "Check my list": the series records the user can open (a Confirmed / Auto linked node in an accessible library, not tombstoned)
    /// by their tracker id on <paramref name="tracker"/>, resolved exactly as <see cref="RemoteIdAsync"/> does (an admin's id first; MangaUpdates
    /// = the linked record's own id; MyAnimeList = <c>mal</c> of the series' MangaDex record or its found companion). Bulk: a few queries per
    /// 500 records.
    /// </summary>
    public async Task<Dictionary<string, List<long>>> RecordsByRemoteIdAsync(long userId, string tracker, CancellationToken ct)
    {
        var accessible = (await libraries.GetAccessibleLibraryIdsAsync(userId, ct)).ToList();
        var states = new[] { (int)SeriesLinkState.Confirmed, (int)SeriesLinkState.Auto };
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var recordIds = await (from l in db.NodeSeriesLinks.AsNoTracking()
                               join n in db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
                               where l.RecordId != null && states.Contains(l.State) && accessible.Contains(n.LibraryId) && n.Availability != tombstoned
                               select l.RecordId!.Value).Distinct().ToListAsync(ct);
        var found = new[] { (int)CompanionState.Auto, (int)CompanionState.Confirmed };
        var result = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var chunk in recordIds.Chunk(Batch))
        {
            var overrides = await db.TrackerIdOverrides.AsNoTracking().Where(o => o.Tracker == tracker && chunk.Contains(o.RecordId))
                .ToDictionaryAsync(o => o.RecordId, o => o.RemoteId, ct);
            var records = await db.MetadataRecords.AsNoTracking().Where(r => chunk.Contains(r.Id))
                .Select(r => new { r.Id, r.Provider, r.ExternalId, r.CrossIdsJson }).ToListAsync(ct);
            var companions = tracker == TrackerSlugs.MyAnimeList
                ? (await (from c in db.MetadataCompanions.AsNoTracking()
                          join r in db.MetadataRecords.AsNoTracking() on c.CompanionRecordId equals r.Id
                          where chunk.Contains(c.RecordId) && c.Provider == MetadataProviderAllowlist.MangaDex && found.Contains(c.State)
                          select new { c.RecordId, r.CrossIdsJson }).ToListAsync(ct))
                    .GroupBy(c => c.RecordId).ToDictionary(g => g.Key, g => g.First().CrossIdsJson)
                : [];
            foreach (var r in records)
            {
                string? id = overrides.TryGetValue(r.Id, out var own) && TrackerFacts.IsValidRemoteId(own) ? own : null;
                id ??= tracker switch
                {
                    TrackerSlugs.MangaUpdates => r.Provider == MetadataProviderAllowlist.MangaUpdates && TrackerFacts.IsValidRemoteId(r.ExternalId) ? r.ExternalId : null,
                    TrackerSlugs.MyAnimeList => CrossId(r.Provider == MetadataProviderAllowlist.MangaDex ? r.CrossIdsJson : companions.GetValueOrDefault(r.Id),
                        TrackerCrossIdKeys.MyAnimeList),
                    _ => null,
                };
                if (id is null)
                    continue;
                if (!result.TryGetValue(id, out var list))
                    result[id] = list = [];
                list.Add(r.Id);
            }
        }
        return result;
    }

    private static string? CrossId(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var id = JsonSerializer.Deserialize<Dictionary<string, string>>(json)?.GetValueOrDefault(key);
            return TrackerFacts.IsValidRemoteId(id) ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The user's progress of <paramref name="recordId"/>: every folder with its own Confirmed / Auto link to the record in a library the
    /// user can open (the linked folder and its unit subfolders, as the Volumes view and the Missing report read them, or a linked archive
    /// itself), only archives that may sync, the user's read marks on them (bulk / import marks held for "Send"), the stored volume map,
    /// and each folder's declared edition. The highest numbers of all folders win.
    /// </summary>
    public async Task<TrackerSeriesProgress> ProgressAsync(long userId, long recordId, CancellationToken ct)
    {
        var scope = await LoadScopeAsync(userId, recordId, ct);
        if (scope is null)
            return TrackerSeriesProgress.Empty;
        var eligible = scope.Marks.Count == 0
            ? new HashSet<long>()
            : await EligibleAsync(userId, scope.Marks.Keys.ToDictionary(id => id, _ => recordId), ct);

        TrackerNumbers auto = TrackerNumbers.None, all = TrackerNumbers.None;
        DateTimeOffset? earliest = null;
        foreach (var folder in scope.Folders)
        {
            var progressRows = new List<TrackerProgressRow>(folder.Rows.Count);
            foreach (var (row, nodeId) in folder.Rows)
            {
                var read = nodeId is { } id && scope.Marks.TryGetValue(id, out var mark) && eligible.Contains(id);
                var held = read && IsHeldSource(scope.Marks[nodeId!.Value].Source);
                if (read && (earliest is null || scope.Marks[nodeId!.Value].At < earliest))
                    earliest = scope.Marks[nodeId!.Value].At;
                progressRows.Add(new TrackerProgressRow(row, read, held));
            }
            var result = TrackerProgress.Compute(progressRows, scope.Map, folder.DeclaredEdition);
            auto = Max(auto, result.Automatic);
            all = Max(all, result.All);
        }
        var startedOn = earliest is { } e ? DateOnly.FromDateTime(e.UtcDateTime) : (DateOnly?)null;
        return new TrackerSeriesProgress(new TrackerProgressResult(auto, all), startedOn, scope.FirstNodePublicId);
    }

    /// <summary>One archive of a series scope as the "from tracker" marking reads it.</summary>
    public sealed record ScopeArchive(long NodeId, UnitNumbers Units, bool DeclaredEdition, bool Read, bool OneShot = false);

    /// <summary>
    /// Every archive of <paramref name="recordId"/>'s scope the user can open (each folder with its own Confirmed / Auto link in a library the
    /// user can open, its unit subfolders, a linked archive itself; once each), with its units (<see cref="VolumeGrouping.UnitsOf"/> after the
    /// series heads), its folder's declared edition and whether the user marked it read; and the record's stored volume map.
    /// 1.43.0 owner answer 2: this scope is NOT narrowed by the outbound sync rules - archives in the user's Private libraries, below the user's
    /// sync-off switch, or of a doujinshi record are in it ("Set progress from &lt;tracker&gt; to MangaPixer" marks them too); only library access
    /// limits it. The outbound rules (<see cref="RecordsOfAsync"/>, <see cref="ProgressAsync"/>) are unchanged.
    /// </summary>
    public async Task<(IReadOnlyList<ScopeArchive> Archives, VolumeMapInput Map)> ArchivesAsync(long userId, long recordId, CancellationToken ct)
    {
        var scope = await LoadScopeAsync(userId, recordId, ct);
        if (scope is null)
            return ([], VolumeMapInput.Empty);
        var result = new Dictionary<long, ScopeArchive>();
        foreach (var folder in scope.Folders)
        {
            // The tracker units (series heads, and the one-shot rule - TrackerUnits).
            var units = TrackerUnits.Of(folder.Rows.Select(r => r.Row).ToList());
            for (var i = 0; i < folder.Rows.Count; i++)
            {
                if (folder.Rows[i].NodeId is not { } id || folder.Rows[i].Row.Kind != GroupingRowKind.Archive || result.ContainsKey(id))
                    continue;
                result[id] = new ScopeArchive(id, units[i].Units, folder.DeclaredEdition, scope.Marks.ContainsKey(id), units[i].OneShot);
            }
        }
        return (result.Values.ToList(), scope.Map);
    }

    /// <summary>
    /// 1.46.0 (lane B, measured cost #1): <see cref="ArchivesAsync"/> for MANY records in one batched walk - the same scope per record (each
    /// folder with its own Confirmed / Auto link in a library the user can open, its unit subfolders, a linked archive itself; once each, the
    /// first folder by node id winning), the same units after the series heads, the same declared editions and the same stored volume map -
    /// in a fixed number of queries for the whole set instead of ~20 per record (the browse cards' series rings ran it once per tracked
    /// series card). <paramref name="readMarks"/>: load <see cref="ScopeArchive.Read"/> (false = left false; the card rings do not read it).
    /// Every record asked for is in the answer; one without a folder the user can open has no archives and an empty map, as in
    /// <see cref="ArchivesAsync"/>.
    /// </summary>
    public async Task<Dictionary<long, (IReadOnlyList<ScopeArchive> Archives, VolumeMapInput Map)>> ArchivesOfRecordsAsync(long userId,
        IReadOnlyCollection<long> recordIds, bool readMarks, CancellationToken ct)
    {
        var result = new Dictionary<long, (IReadOnlyList<ScopeArchive>, VolumeMapInput)>();
        var wanted = recordIds.Distinct().ToList();
        if (wanted.Count == 0)
            return result;
        foreach (var id in wanted)
            result[id] = ([], VolumeMapInput.Empty);
        var perfPhase = com.lifepixer.mangapixer.Server.Operations.Perf.PerfPhase.Start(); // measurement only (lane M's laps)

        var accessible = (await libraries.GetAccessibleLibraryIdsAsync(userId, ct)).ToList();
        var states = new[] { (int)SeriesLinkState.Confirmed, (int)SeriesLinkState.Auto };
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var linkNodes = new List<(long RecordId, long Id, string PublicId, int Kind, long? ParentId, string DisplayName, string SortKey)>();
        foreach (var chunk in wanted.Chunk(Batch))
        {
            var rows = await (from l in db.NodeSeriesLinks.AsNoTracking()
                              join n in db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
                              where l.RecordId != null && chunk.Contains(l.RecordId.Value) && states.Contains(l.State)
                                    && accessible.Contains(n.LibraryId) && n.Availability != tombstoned
                              select new { RecordId = l.RecordId!.Value, n.Id, n.PublicId, n.Kind, n.ParentId, n.DisplayName, n.SortKey })
                .ToListAsync(ct);
            linkNodes.AddRange(rows.Select(r => (r.RecordId, r.Id, r.PublicId, r.Kind, r.ParentId, r.DisplayName, r.SortKey)));
        }
        if (linkNodes.Count == 0)
            return result;
        // Per record in node id order (LoadScopeAsync's `orderby n.Id`: the first folder holding an archive names its units).
        var byRecord = linkNodes.OrderBy(n => n.Id).GroupBy(n => n.RecordId).ToDictionary(g => g.Key, g => g.ToList());

        // Rows per link node: a folder's series scope - the rows SeriesProgressLoader gives (its walk, ComicInfo and the volume contents
        // scan), read for every folder of every record at once without the progress answers the rings do not need - or a linked archive
        // on its own.
        var archive = (int)CatalogNodeKind.Archive;
        var folders = linkNodes.Where(n => n.Kind != archive).ToList();
        var maps = await MapInputsAsync(db, byRecord.Keys.ToList(), ct);
        perfPhase = perfPhase.Lap("marks.walk-links");
        var (rowsByNode, nodeIds) = await ScopeRowsAsync(db, folders.Select(f => (f.Id, maps[f.RecordId])).ToList(), ct);
        perfPhase = perfPhase.Lap("marks.walk-series-scopes");
        var parentIds = linkNodes.Where(n => n.Kind == archive && n.ParentId is not null).Select(n => n.ParentId!.Value).Distinct().ToList();
        var parentNames = new Dictionary<long, string>();
        foreach (var chunk in parentIds.Chunk(Batch))
        {
            foreach (var p in await db.CatalogNodes.AsNoTracking().Where(n => chunk.Contains(n.Id)).Select(n => new { n.Id, n.DisplayName }).ToListAsync(ct))
                parentNames[p.Id] = p.DisplayName;
        }
        foreach (var a in linkNodes.Where(n => n.Kind == archive))
        {
            rowsByNode[a.Id] = [new GroupingRow(a.PublicId, GroupingRowKind.Archive, a.DisplayName, a.SortKey,
                a.ParentId is { } p ? parentNames.GetValueOrDefault(p) : null)];
        }

        // Archive public id -> node id: the walk's archives, and each linked archive (the user's marks when asked).
        foreach (var a in linkNodes.Where(n => n.Kind == archive))
            nodeIds[a.PublicId] = a.Id;
        var marked = new HashSet<long>();
        if (readMarks)
        {
            foreach (var chunk in nodeIds.Values.Distinct().Chunk(Batch))
                marked.UnionWith(await db.ReadMarks.AsNoTracking().Where(m => m.UserId == userId && chunk.Contains(m.ItemId)).Select(m => m.ItemId)
                    .ToListAsync(ct));
        }

        // Each link node's declared edition.
        var editions = new Dictionary<long, DeclaredEditionFacts>();
        foreach (var chunk in linkNodes.Select(n => n.Id).Distinct().Chunk(Batch))
        {
            foreach (var (node, edition) in await DeclaredFactsResolution.LoadEditionAsync(db, chunk.ToList(), ct))
                editions[node] = edition;
        }

        perfPhase = perfPhase.Lap("marks.walk-ids-maps-editions");
        foreach (var (recordId, nodes) in byRecord)
        {
            var archives = new Dictionary<long, ScopeArchive>();
            foreach (var node in nodes)
            {
                var edition = editions.GetValueOrDefault(node.Id);
                var declared = edition is { VolumeTotal: not null } or { Edition: not null };
                var rows = rowsByNode[node.Id];
                // The tracker units (series heads, and the one-shot rule - TrackerUnits), as ArchivesAsync reads each folder.
                var units = TrackerUnits.Of(rows.ToList());
                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Kind != GroupingRowKind.Archive || !nodeIds.TryGetValue(rows[i].Id, out var id) || archives.ContainsKey(id))
                        continue;
                    archives[id] = new ScopeArchive(id, units[i].Units, declared, marked.Contains(id), units[i].OneShot);
                }
            }
            result[recordId] = (archives.Values.ToList(), maps[recordId]);
        }
        perfPhase.Lap("marks.walk-units");
        return result;
    }

    /// <summary>
    /// The archive rows of each series folder exactly as <see cref="SeriesProgressLoader.LoadAsync"/> returns them in
    /// <see cref="SeriesProgressEntry.Rows"/> (the same walk: the folder and its unit subfolders - unit-named, not side material, not linked
    /// on their own - up to <see cref="SeriesProgressLoader.MaxUnitDepth"/> levels, children by SortKey, tombstoned nodes left out; ComicInfo
    /// volume / number; the volume contents scan applied with the record's list map), batched over every folder, without the progress
    /// answers. Also the node id of every archive it lists, by public id. <paramref name="folders"/>: (series folder, its record's map).
    /// </summary>
    private static async Task<(Dictionary<long, IReadOnlyList<GroupingRow>> Rows, Dictionary<string, long> NodeIds)> ScopeRowsAsync(
        MangaPixerDbContext db, IReadOnlyList<(long NodeId, VolumeMapInput Map)> folders, CancellationToken ct)
    {
        var rows = new Dictionary<long, IReadOnlyList<GroupingRow>>();
        var nodeIds = new Dictionary<string, long>(StringComparer.Ordinal);
        if (folders.Count == 0)
            return (rows, nodeIds);

        // The series scope, one level at a time (SeriesProgressLoader's walk).
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var archive = (int)CatalogNodeKind.Archive;
        var scope = folders.DistinctBy(f => f.NodeId).ToDictionary(f => f.NodeId, f => (Series: f.NodeId, Name: (string?)null));
        var archivesIn = new Dictionary<long, List<(long Id, string PublicId, string Name, string SortKey)>>();
        var frontier = scope.Keys.ToList();
        for (var depth = 0; depth <= SeriesProgressLoader.MaxUnitDepth && frontier.Count > 0; depth++)
        {
            var level = frontier;
            var children = await db.CatalogNodes.AsNoTracking()
                .Where(n => n.ParentId != null && level.Contains(n.ParentId.Value) && n.Availability != tombstoned)
                .OrderBy(n => n.SortKey)
                .Select(n => new { n.Id, ParentId = n.ParentId!.Value, n.Kind, n.DisplayName, n.PublicId, n.SortKey })
                .ToListAsync(ct);
            foreach (var c in children.Where(c => c.Kind == archive))
            {
                if (!archivesIn.TryGetValue(c.ParentId, out var list))
                    archivesIn[c.ParentId] = list = [];
                list.Add((c.Id, c.PublicId, c.DisplayName, c.SortKey));
            }
            if (depth == SeriesProgressLoader.MaxUnitDepth)
                break;
            var units = children
                .Where(c => c.Kind != archive && AutoMatchText.IsUnitFolderName(c.DisplayName) && !CountEvidence.IsSideFolderName(c.DisplayName))
                .ToList();
            var unitIds = units.Select(u => u.Id).ToList();
            var ownLinked = (await db.NodeSeriesLinks.AsNoTracking().Where(l => unitIds.Contains(l.NodeId)).Select(l => l.NodeId).ToListAsync(ct))
                .ToHashSet();
            frontier = [];
            foreach (var u in units.Where(u => !ownLinked.Contains(u.Id)))
            {
                scope[u.Id] = (scope[u.ParentId].Series, u.DisplayName);
                frontier.Add(u.Id);
            }
        }

        // ComicInfo Volume / Number of the archives directly below the scope's folders (parsed ComicInfo only).
        var parentIds = scope.Keys.ToList();
        var comicInfo = (await (
                from e in db.EmbeddedMetadata.AsNoTracking()
                join n in db.CatalogNodes.AsNoTracking() on e.NodeId equals n.Id
                where n.ParentId != null && parentIds.Contains(n.ParentId.Value) && e.State == 1 && (e.Volume != null || e.Number != null)
                select new { e.NodeId, e.Volume, e.Number }).ToListAsync(ct))
            .ToDictionary(r => r.NodeId, r => (r.Volume, (string?)r.Number));

        var bySeries = scope.Select(kv => (Id: kv.Key, kv.Value.Series, kv.Value.Name)).ToLookup(f => f.Series);
        foreach (var (target, map) in folders.DistinctBy(f => f.NodeId))
        {
            var built = new List<GroupingRow>();
            var archiveIds = new Dictionary<long, string>();
            foreach (var folder in bySeries[target])
            {
                foreach (var a in archivesIn.GetValueOrDefault(folder.Id) ?? [])
                {
                    var ci = comicInfo.GetValueOrDefault(a.Id);
                    built.Add(new GroupingRow(a.PublicId, GroupingRowKind.Archive, a.Name, a.SortKey, folder.Name, ci.Volume, ci.Number));
                    archiveIds[a.Id] = a.PublicId;
                    nodeIds[a.PublicId] = a.Id;
                }
            }
            // 1.44.0: the volume contents scan's answers, as the Volumes view reads them (VolumeContentReader, VolumeContents.Apply).
            var (applied, _) = VolumeContents.Apply(built, map,
                VolumeContents.MayApply(built) ? await VolumeScan.VolumeContentReader.LoadAsync(db, archiveIds, ct) : []);
            rows[target] = applied;
        }
        return (rows, nodeIds);
    }

    /// <summary>
    /// <see cref="MapAsync(MangaPixerDbContext, long, CancellationToken)"/>'s map for many records (every record asked for is in the answer,
    /// one without stored maps with the map its record fields give, as there).
    /// </summary>
    private static async Task<Dictionary<long, VolumeMapInput>> MapInputsAsync(MangaPixerDbContext db, IReadOnlyCollection<long> recordIds,
        CancellationToken ct)
    {
        var result = new Dictionary<long, VolumeMapInput>();
        if (recordIds.Count == 0)
            return result;
        var language = await ReleasedInLanguage.PreferredAsync(db, ct);
        foreach (var chunk in recordIds.Distinct().Chunk(Batch))
        {
            var maps = (await db.SeriesVolumeMaps.AsNoTracking().Where(m => chunk.Contains(m.RecordId)).ToListAsync(ct)).ToLookup(m => m.RecordId);
            var records = await db.MetadataRecords.AsNoTracking().Where(r => chunk.Contains(r.Id))
                .Select(r => new SeriesProgressLoader.RecordRow(r.Id, r.Origin, r.OriginStatus, r.OriginVolumes, r.StatusText, r.LatestChapter,
                    r.PublishersJson, r.LicensedEn, r.TranslationComplete, r.Webtoon))
                .ToDictionaryAsync(r => r.Id, ct);
            foreach (var id in chunk)
                result[id] = SeriesProgressLoader.MapAndFacts(maps[id].ToList(), records.GetValueOrDefault(id), language).Map;
        }
        return result;
    }

    private sealed record ScopeFolder(IReadOnlyList<(GroupingRow Row, long? NodeId)> Rows, bool DeclaredEdition);

    private sealed record Scope(
        IReadOnlyList<ScopeFolder> Folders,
        IReadOnlyDictionary<long, (string Source, DateTimeOffset At)> Marks,
        VolumeMapInput Map,
        string FirstNodePublicId);

    /// <summary>The rows of every linked folder of the record the user can open, the user's marks on them, the map and the editions.</summary>
    private async Task<Scope?> LoadScopeAsync(long userId, long recordId, CancellationToken ct)
    {
        var accessible = (await libraries.GetAccessibleLibraryIdsAsync(userId, ct)).ToList();
        var states = new[] { (int)SeriesLinkState.Confirmed, (int)SeriesLinkState.Auto };
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var linkNodes = await (from l in db.NodeSeriesLinks.AsNoTracking()
                               join n in db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
                               where l.RecordId == recordId && states.Contains(l.State) && accessible.Contains(n.LibraryId)
                                     && n.Availability != tombstoned
                               orderby n.Id
                               select new { n.Id, n.PublicId, n.Kind, n.ParentId, n.DisplayName, n.SortKey }).ToListAsync(ct);
        if (linkNodes.Count == 0)
            return null;

        // Rows per link node: a folder's series scope (SeriesProgressLoader), or a linked archive on its own.
        var archive = (int)CatalogNodeKind.Archive;
        var folders = linkNodes.Where(n => n.Kind != archive).ToList();
        var scopes = await new SeriesProgressLoader(db).LoadAsync(folders.Select(f => new SeriesProgressTarget(f.Id, recordId)).ToList(), ct);
        var rowsByNode = new Dictionary<long, IReadOnlyList<GroupingRow>>();
        foreach (var f in folders)
            rowsByNode[f.Id] = scopes.TryGetValue(f.Id, out var entry) ? entry.Rows : [];
        var parentIds = linkNodes.Where(n => n.Kind == archive && n.ParentId is not null).Select(n => n.ParentId!.Value).ToList();
        var parentNames = await db.CatalogNodes.AsNoTracking().Where(n => parentIds.Contains(n.Id))
            .Select(n => new { n.Id, n.DisplayName }).ToDictionaryAsync(n => n.Id, n => n.DisplayName, ct);
        foreach (var a in linkNodes.Where(n => n.Kind == archive))
        {
            rowsByNode[a.Id] = [new GroupingRow(a.PublicId, GroupingRowKind.Archive, a.DisplayName, a.SortKey,
                a.ParentId is { } p ? parentNames.GetValueOrDefault(p) : null)];
        }

        // Archive public id -> node id, and the marks of the user.
        var publicIds = rowsByNode.Values.SelectMany(r => r).Select(r => r.Id).Distinct().ToList();
        var nodeIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var chunk in publicIds.Chunk(Batch))
        {
            foreach (var n in await db.CatalogNodes.AsNoTracking().Where(n => chunk.Contains(n.PublicId))
                         .Select(n => new { n.Id, n.PublicId }).ToListAsync(ct))
                nodeIds[n.PublicId] = n.Id;
        }
        var allIds = nodeIds.Values.ToList();
        var marks = new Dictionary<long, (string Source, DateTimeOffset At)>();
        foreach (var chunk in allIds.Chunk(Batch))
        {
            foreach (var m in await db.ReadMarks.AsNoTracking().Where(m => m.UserId == userId && chunk.Contains(m.ItemId))
                         .Select(m => new { m.ItemId, m.Source, m.MarkedAt }).ToListAsync(ct))
                marks[m.ItemId] = (m.Source, m.MarkedAt);
        }

        // The map of the record, and each folder's declared edition.
        var (map, _) = await MapAsync(recordId, ct);
        var editions = await DeclaredFactsResolution.LoadEditionAsync(db, linkNodes.Select(n => n.Id).ToList(), ct);
        var scopeFolders = linkNodes.Select(node =>
        {
            var edition = editions.GetValueOrDefault(node.Id);
            var declared = edition is { VolumeTotal: not null } or { Edition: not null };
            var rows = rowsByNode[node.Id].Select(r => (r, nodeIds.TryGetValue(r.Id, out var id) ? id : (long?)null)).ToList();
            return new ScopeFolder(rows, declared);
        }).ToList();
        return new Scope(scopeFolders, marks, map, linkNodes[0].PublicId);
    }

    /// <summary>
    /// 1.43.0 (lane G): the node behind a public id when it is in a library the user can open (not tombstoned), with the record of its
    /// nearest Confirmed / Auto link (as <see cref="RecordsOfAsync"/> reads an archive's) - null record when it has none.
    /// </summary>
    public async Task<(long NodeId, long? RecordId)?> SeriesOfNodeAsync(long userId, string nodePublicId, CancellationToken ct)
    {
        var accessible = (await libraries.GetAccessibleLibraryIdsAsync(userId, ct)).ToList();
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var node = await db.CatalogNodes.AsNoTracking()
            .Where(n => n.PublicId == nodePublicId && accessible.Contains(n.LibraryId) && n.Availability != tombstoned)
            .Select(n => (long?)n.Id).FirstOrDefaultAsync(ct);
        if (node is not { } id)
            return null;
        var links = await CoverLinks.NearestAsync(db, [id], ct);
        return (id, links.TryGetValue(id, out var link) && link.IsLinked ? link.RecordId : null);
    }

    /// <summary>
    /// 1.43.0 (lane G, series folder cards): the record of each node's OWN Confirmed / Auto link (no ancestor walk - a folder card stands for a
    /// series only when the link is its own), by the node's public id; nodes without one are left out. No access check (the caller browses).
    /// </summary>
    public static async Task<Dictionary<string, long>> OwnLinkedRecordsAsync(MangaPixerDbContext db, IReadOnlyCollection<string> nodePublicIds,
        CancellationToken ct)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (nodePublicIds.Count == 0)
            return result;
        var states = new[] { (int)SeriesLinkState.Confirmed, (int)SeriesLinkState.Auto };
        foreach (var chunk in nodePublicIds.Distinct().Chunk(Batch))
        {
            var rows = await (from n in db.CatalogNodes.AsNoTracking()
                              join l in db.NodeSeriesLinks.AsNoTracking() on n.Id equals l.NodeId
                              where chunk.Contains(n.PublicId) && l.RecordId != null && states.Contains(l.State)
                              select new { n.PublicId, RecordId = l.RecordId!.Value }).ToListAsync(ct);
            foreach (var row in rows)
                result[row.PublicId] = row.RecordId;
        }
        return result;
    }

    /// <summary>1.43.0 (lane G): per record, the first folder (or archive) with its own Confirmed / Auto link the user can open.</summary>
    public async Task<Dictionary<long, string>> FirstNodesAsync(long userId, IReadOnlyCollection<long> recordIds, CancellationToken ct)
    {
        var result = new Dictionary<long, string>();
        if (recordIds.Count == 0)
            return result;
        var accessible = (await libraries.GetAccessibleLibraryIdsAsync(userId, ct)).ToList();
        var states = new[] { (int)SeriesLinkState.Confirmed, (int)SeriesLinkState.Auto };
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        foreach (var chunk in recordIds.Distinct().Chunk(Batch))
        {
            var rows = await (from l in db.NodeSeriesLinks.AsNoTracking()
                              join n in db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
                              where l.RecordId != null && chunk.Contains(l.RecordId.Value) && states.Contains(l.State)
                                    && accessible.Contains(n.LibraryId) && n.Availability != tombstoned
                              orderby n.Id
                              select new { RecordId = l.RecordId!.Value, n.PublicId }).ToListAsync(ct);
            foreach (var row in rows)
                result.TryAdd(row.RecordId, row.PublicId);
        }
        return result;
    }

    /// <summary>Marks that wait for the user's "Send" (owner Q4): a folder mark-read and a YACReader import.</summary>
    public static bool IsHeldSource(string source) => source is "bulk" or "import";

    /// <summary>
    /// 1.43.0 (lane G): the source of a mark made by "Set progress from &lt;tracker&gt; to MangaPixer". Such a mark never triggers an
    /// outbound sync (the relay ignores it); it counts in the numbers like a manual mark (it is at or below the tracker's numbers).
    /// </summary>
    public const string TrackerMarkSource = "tracker";

    /// <summary>The record's stored volume map (as every reader sees it) and the record's progress fields.</summary>
    public Task<(VolumeMapInput Map, SeriesProgressLoader.RecordRow? Record)> MapAsync(long recordId, CancellationToken ct) =>
        MapAsync(db, recordId, ct);

    /// <summary>
    /// 1.43.0 (lane G): the record of a node's nearest Confirmed / Auto link (the same walk as <see cref="RecordsOfAsync"/>), or null - for
    /// the browse cards' "read on &lt;tracker&gt;" mark (no access check here: the caller browses a folder it may open).
    /// </summary>
    public static async Task<long?> LinkedRecordOfAsync(MangaPixerDbContext db, long nodeId, CancellationToken ct)
    {
        var links = await CoverLinks.NearestAsync(db, [nodeId], ct);
        return links.TryGetValue(nodeId, out var link) && link.IsLinked ? link.RecordId : null;
    }

    /// <summary>The record's stored volume map over any context (see the instance method).</summary>
    public static async Task<(VolumeMapInput Map, SeriesProgressLoader.RecordRow? Record)> MapAsync(MangaPixerDbContext db, long recordId,
        CancellationToken ct)
    {
        var maps = await db.SeriesVolumeMaps.AsNoTracking().Where(m => m.RecordId == recordId).ToListAsync(ct);
        var record = await db.MetadataRecords.AsNoTracking().Where(r => r.Id == recordId)
            .Select(r => new SeriesProgressLoader.RecordRow(r.Id, r.Origin, r.OriginStatus, r.OriginVolumes, r.StatusText, r.LatestChapter,
                r.PublishersJson, r.LicensedEn, r.TranslationComplete, r.Webtoon))
            .FirstOrDefaultAsync(ct);
        var language = await ReleasedInLanguage.PreferredAsync(db, ct);
        var (map, _) = SeriesProgressLoader.MapAndFacts(maps, record, language);
        return (map, record);
    }

    /// <summary>1.44.0: <see cref="MapAsync"/> for many records at once (the Tracking list's "volumes from chapters" offers), by record id.</summary>
    public static async Task<Dictionary<long, VolumeMapInput>> MapsAsync(MangaPixerDbContext db, IReadOnlyCollection<long> recordIds, CancellationToken ct)
    {
        var result = new Dictionary<long, VolumeMapInput>();
        if (recordIds.Count == 0)
            return result;
        var language = await ReleasedInLanguage.PreferredAsync(db, ct);
        foreach (var chunk in recordIds.Distinct().Chunk(Batch))
        {
            var maps = (await db.SeriesVolumeMaps.AsNoTracking().Where(m => chunk.Contains(m.RecordId)).ToListAsync(ct)).ToLookup(m => m.RecordId);
            var records = await db.MetadataRecords.AsNoTracking().Where(r => chunk.Contains(r.Id))
                .Select(r => new SeriesProgressLoader.RecordRow(r.Id, r.Origin, r.OriginStatus, r.OriginVolumes, r.StatusText, r.LatestChapter,
                    r.PublishersJson, r.LicensedEn, r.TranslationComplete, r.Webtoon))
                .ToDictionaryAsync(r => r.Id, ct);
            foreach (var id in chunk)
            {
                if (!maps.Contains(id))
                    continue;
                result[id] = SeriesProgressLoader.MapAndFacts(maps[id].ToList(), records.GetValueOrDefault(id), language).Map;
            }
        }
        return result;
    }

    /// <summary>
    /// The series totals a tracker's entry lacks, from the stored record (MangaUpdates list entries carry none): finished only when the
    /// record's origin status is Complete; the chapter total from its status text, the volume total from its origin volumes.
    /// </summary>
    public static (int? Chapters, int? Volumes, bool Finished) TotalsOf(SeriesProgressLoader.RecordRow? record)
    {
        if (record is null || (MetadataOriginStatus?)record.OriginStatus != MetadataOriginStatus.Complete)
            return (null, null, false);
        return (MangaUpdatesStatusParser.Parse(record.StatusText).Chapters, record.OriginVolumes, true);
    }

    private static TrackerNumbers Max(TrackerNumbers a, TrackerNumbers b) =>
        new(a.Chapters is null ? b.Chapters : b.Chapters is null ? a.Chapters : Math.Max(a.Chapters.Value, b.Chapters.Value),
            a.Volumes is null ? b.Volumes : b.Volumes is null ? a.Volumes : Math.Max(a.Volumes.Value, b.Volumes.Value));
}
