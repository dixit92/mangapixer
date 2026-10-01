namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch.LinkCoverCheck;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Process-wide memory of the cover check (singleton): the inputs each Auto link was last checked with (so unchanged links are not
/// hashed again every tick) and the hashes of stored spread crops. Memory only - after a restart every link is checked once more.
/// </summary>
public sealed class CoverCheckState
{
    public const int Capacity = 4096;

    private readonly Dictionary<(long NodeId, long RecordId), string> _checked = [];
    private (string Fingerprint, DateTimeOffset At)? _lastSweep;
    private readonly Dictionary<(long ItemId, long ContentVersion, CoverCropSide Side), ulong> _crops = [];
    private readonly Queue<(long, long, CoverCropSide)> _cropOrder = new();
    private readonly object _gate = new();

    public bool IsUnchanged(long nodeId, long recordId, string inputs)
    {
        lock (_gate)
            return _checked.TryGetValue((nodeId, recordId), out var last) && last == inputs;
    }

    public void Remember(long nodeId, long recordId, string inputs)
    {
        lock (_gate)
        {
            if (_checked.Count >= Capacity && !_checked.ContainsKey((nodeId, recordId)))
                _checked.Clear();
            _checked[(nodeId, recordId)] = inputs;
        }
    }

    /// <summary>
    /// True when a full sweep is not needed: the stored covers and the Auto links are as at the last full sweep, that sweep left
    /// nothing pending, and it is less than <paramref name="every"/> old (a thumbnail or crop made since is picked up then).
    /// </summary>
    public bool CanSkipSweep(string fingerprint, DateTimeOffset now, TimeSpan every)
    {
        lock (_gate)
            return _lastSweep is { } last && last.Fingerprint == fingerprint && now - last.At < every;
    }

    /// <summary>Records a full sweep; <paramref name="pending"/> (the per-sweep limit was reached, or a hash was missing) forces the next one.</summary>
    public void SweepDone(string fingerprint, DateTimeOffset now, bool pending)
    {
        lock (_gate)
            _lastSweep = pending ? null : (fingerprint, now);
    }

    public bool TryGetCrop(long itemId, long contentVersion, CoverCropSide side, out ulong hash)
    {
        lock (_gate)
            return _crops.TryGetValue((itemId, contentVersion, side), out hash);
    }

    public void SetCrop(long itemId, long contentVersion, CoverCropSide side, ulong hash)
    {
        lock (_gate)
        {
            if (!_crops.TryAdd((itemId, contentVersion, side), hash))
                return;
            _cropOrder.Enqueue((itemId, contentVersion, side));
            while (_cropOrder.Count > Capacity)
                _crops.Remove(_cropOrder.Dequeue());
        }
    }
}

/// <summary>What one sweep did (counts only): links that got a verdict (agrees, unsure or differs) and links moved to review.</summary>
public sealed record CoverCheckSweepResult(int Checked, int Demoted);

/// <summary>
/// The cover check AFTER an automatic link (1.31.0; owner 2026-09-30: covers are a positive tie-break before linking, a veto only
/// with per-volume, per-language covers after it). For each AUTO link whose record's MangaDex companion has volume covers stored
/// (by the approved volume-cover work), the folder's own volume covers - page 1 of each volume archive, both halves of a spread
/// page 1 - are compared with the stored covers of the SAME volume (<see cref="CoverCheckRule"/>): a cover in any language can agree,
/// only one in the preferred language (the language the folder's releases are read in) can count against the link. When every
/// compared volume is clearly a different picture, the link moves back to Needs review with the record as its only candidate and
/// the <see cref="MatchReason.CoverDiffers"/> chip (the <see cref="Reach.ReachCheckService"/> shape). Confirmed links are never
/// touched. Never sends a request: stored covers, stored thumbnails and stored crops only, hashed by the media worker. Off with
/// "Compare covers" (or <c>Metadata:AutoMatch:CompareCovers=false</c>). Logs ids, counts, the verdict and the measured distances
/// (numbers) only - never names, titles or paths.
/// </summary>
public sealed class CoverCheckService(
    MangaPixerDbContext db,
    ICoverHasher hasher,
    ThumbnailStore thumbnails,
    CoverFiles files,
    CoverHashCache hashCache,
    CoverCheckState state,
    ICoverCompareSetting setting,
    TimeProvider time,
    ILogger<CoverCheckService> logger)
{
    /// <summary>At most this many links are evaluated (hashed) per sweep; unchanged links do not count.</summary>
    public const int MaxLinksPerSweep = 20;

    /// <summary>
    /// With no new stored cover and no changed Auto link, a full sweep (which reads every Auto link's archives) runs at most this
    /// often - for thumbnails and crops made since.
    /// </summary>
    public static readonly TimeSpan FullSweepEvery = TimeSpan.FromMinutes(30);

    private sealed record LinkRow(long NodeId, long RecordId, long CompanionId, DateTimeOffset LinkedAt);

    private sealed record WebCover(long Id, int Kind, int? Volume, string Locale, int StoredVersion, ulong Hash);

    /// <summary>One sweep over the Auto links with stored covers (newest link first). Never throws for a single link's failure.</summary>
    public async Task<CoverCheckSweepResult> SweepAsync(CancellationToken ct = default)
    {
        if (!await setting.IsEnabledAsync(ct))
            return new CoverCheckSweepResult(0, 0);
        var fingerprint = await FingerprintAsync(ct);
        if (state.CanSkipSweep(fingerprint, time.GetUtcNow(), FullSweepEvery))
            return new CoverCheckSweepResult(0, 0);
        var links = await AutoLinksWithCoversAsync(null, ct);
        int evaluated = 0, decided = 0, demoted = 0;
        var pending = false;
        foreach (var link in links)
        {
            if (evaluated >= MaxLinksPerSweep)
            {
                pending = true;
                break;
            }
            try
            {
                var (outcome, incomplete) = await CheckAsync(link, ct);
                pending |= incomplete;
                if (outcome is null)
                    continue;
                evaluated++;
                if (outcome.Verdict != CoverCheckVerdict.NotEnough)
                    decided++;
                if (outcome.Demotes)
                    demoted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(LogEvents.Metadata.CoverCheckFailed, "Cover check of node {NodeId} failed: {Error}", link.NodeId, ex.GetType().Name);
            }
        }
        state.SweepDone(fingerprint, time.GetUtcNow(), pending);
        return new CoverCheckSweepResult(decided, demoted);
    }

    /// <summary>
    /// Checks one node's Auto link now (inputs memory ignored). Null when there is nothing to check (no Auto link, no stored
    /// covers, not volume-shaped, the setting off).
    /// </summary>
    public async Task<CoverCheckResult?> CheckNodeAsync(long nodeId, CancellationToken ct = default)
    {
        if (!await setting.IsEnabledAsync(ct))
            return null;
        var link = (await AutoLinksWithCoversAsync(nodeId, ct)).FirstOrDefault();
        return link is null ? null : (await CheckAsync(link, ct, force: true)).Result;
    }

    /// <summary>What a sweep depends on besides local files: the stored hashed covers and the Auto links (counts and id sums).</summary>
    private async Task<string> FingerprintAsync(CancellationToken ct)
    {
        var stored = (int)VolumeCoverState.Stored;
        var auto = (int)SeriesLinkState.Auto;
        var covers = await db.VolumeCovers.AsNoTracking().Where(c => c.State == stored && c.Hash != null)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Ids = g.Sum(c => c.Id), Versions = g.Sum(c => c.StoredVersion) })
            .FirstOrDefaultAsync(ct);
        var links = await db.NodeSeriesLinks.AsNoTracking().Where(l => l.State == auto && l.RecordId != null)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Nodes = g.Sum(l => l.NodeId), Records = g.Sum(l => l.RecordId!.Value) })
            .FirstOrDefaultAsync(ct);
        return string.Create(CultureInfo.InvariantCulture,
            $"{covers?.Count}.{covers?.Ids}.{covers?.Versions}|{links?.Count}.{links?.Nodes}.{links?.Records}");
    }

    private async Task<List<LinkRow>> AutoLinksWithCoversAsync(long? nodeId, CancellationToken ct)
    {
        var auto = (int)SeriesLinkState.Auto;
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var stored = (int)VolumeCoverState.Stored;
        var companionAuto = (int)CompanionState.Auto;
        var companionConfirmed = (int)CompanionState.Confirmed;
        var query =
            from l in db.NodeSeriesLinks.AsNoTracking()
            where l.State == auto && l.RecordId != null
            join n in db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
            where n.Availability != tombstoned
            join c in db.MetadataCompanions.AsNoTracking() on l.RecordId equals c.RecordId
            where c.Provider == CompanionLinkService.MangaDex && c.CompanionRecordId != null
                && (c.State == companionAuto || c.State == companionConfirmed)
                && db.VolumeCovers.Any(v => v.ProviderRecordId == c.CompanionRecordId && v.State == stored && v.Hash != null)
            orderby l.UpdatedAt descending, l.NodeId
            select new { l.NodeId, RecordId = l.RecordId!.Value, CompanionId = c.CompanionRecordId!.Value, l.UpdatedAt };
        if (nodeId is { } only)
            query = query.Where(x => x.NodeId == only);
        var rows = await query.ToListAsync(ct);
        return rows.Select(r => new LinkRow(r.NodeId, r.RecordId, r.CompanionId, r.UpdatedAt)).ToList();
    }

    /// <summary>
    /// Gathers, decides and acts for one link; a null result when skipped (not checkable, or its inputs did not change).
    /// <c>Incomplete</c>: a local image could not be hashed now (the worker was busy) - checked again on the next sweep.
    /// </summary>
    private async Task<(CoverCheckResult? Result, bool Incomplete)> CheckAsync(LinkRow link, CancellationToken ct, bool force = false)
    {
        var archives = await VolumeCoverPass.ArchivesBelowAsync(db, [link.NodeId], ct);
        var shaped = archives.Select(a => (Archive: a, Units: VolumeGrouping.UnitsOf(a.Row))).ToList();
        // A one-shot is a single archive that states no number at all; a single "v01" is one volume of a series (it needs a second).
        var oneShot = shaped.Count == 1 && shaped[0].Units is { Chapter: null, Volume: null };
        var byVolume = new SortedDictionary<int, List<VolumeCoverPass.HeldArchive>>();
        foreach (var (archive, units) in shaped)
        {
            int? volume = oneShot ? 1
                : units.Chapter is null && units.VolumeEnd is null && units.Volume is { } w && decimal.Truncate(w) == w && w >= 1 ? (int)w : null;
            if (volume is not { } key)
                continue;
            if (!byVolume.TryGetValue(key, out var list))
                byVolume[key] = list = [];
            list.Add(archive);
        }
        if (byVolume.Count == 0 || (!oneShot && byVolume.Count < CoverCheckRule.MinSeriesVolumes))
            return (null, false); // Chapters, webtoons, a lone volume among chapters: page 1 is not a volume cover.

        var web = (await db.VolumeCovers.AsNoTracking()
                .Where(c => c.ProviderRecordId == link.CompanionId && c.State == (int)VolumeCoverState.Stored && c.Hash != null)
                .Select(c => new { c.Id, c.Kind, c.Volume, c.Locale, c.StoredVersion, c.Hash })
                .ToListAsync(ct))
            .Select(c => new WebCover(c.Id, c.Kind, c.Volume, c.Locale, c.StoredVersion, unchecked((ulong)c.Hash!.Value)))
            .OrderBy(c => c.Id)
            .ToList();
        var preferred = await PreferredLanguageAsync(ct);
        // The covers of a volume in any language (they can only AGREE); the main cover stands in for a one-shot's volume 1 (as the
        // volume-cover work stores it).
        List<WebCover> CoversOf(int volume)
        {
            var own = web.Where(c => c.Kind == (int)VolumeCoverKind.Volume && c.Volume == volume).ToList();
            return own.Count == 0 && oneShot && volume == 1 ? web.Where(c => c.Kind == (int)VolumeCoverKind.Main).ToList() : own;
        }
        // Only a cover in the PREFERRED language - the language the folder's own releases are read in - can count against the link:
        // the same art under another language's title and logo lands 20-30 bits away (fixture check, 2026-10-01).
        List<ulong> VetoOf(int volume) =>
            CoversOf(volume).Where(c => string.Equals(c.Locale, preferred, StringComparison.OrdinalIgnoreCase)).Select(c => c.Hash).ToList();
        var chosen = byVolume.Where(v => CoversOf(v.Key).Count > 0)
            .OrderByDescending(v => VetoOf(v.Key).Count > 0).ThenBy(v => v.Key)
            .Take(CoverCheckRule.MaxVolumes).OrderBy(v => v.Key).ToList();
        if (chosen.Count == 0)
            return (null, false);

        var spreads = new Dictionary<long, bool>();
        foreach (var archive in chosen.SelectMany(v => v.Value))
            spreads[archive.Id] = await VolumeCoverPass.Page1AspectAsync(db, archive, ct) is >= VolumeCoverPass.SpreadAspect;
        var inputs = InputsKey(link, preferred, web, chosen.SelectMany(v => v.Value), spreads);
        if (!force && state.IsUnchanged(link.NodeId, link.RecordId, inputs))
            return (null, false);

        var incomplete = false;
        var volumes = new List<CoverCheckVolume>();
        foreach (var (volume, held) in chosen)
        {
            var local = new List<ulong>();
            foreach (var archive in held)
            {
                var (hashes, missed) = await LocalHashesAsync(archive, spreads[archive.Id], ct);
                local.AddRange(hashes);
                incomplete |= missed;
            }
            volumes.Add(new CoverCheckVolume(volume, local, VetoOf(volume)));
        }
        var result = CoverCheckRule.Decide(volumes, web.Select(c => c.Hash).ToList(), oneShot);
        if (!incomplete)
            state.Remember(link.NodeId, link.RecordId, inputs);

        var demoted = result.Demotes && await DemoteAsync(link, ct);
        if (result.Verdict != CoverCheckVerdict.NotEnough)
        {
            logger.LogInformation(LogEvents.Metadata.CoverCheckDecided,
                "Cover check: node {NodeId} record {RecordId}: {Verdict}, {Compared} volumes compared (distances {Distances}), {Dropped} dropped, {Action}",
                link.NodeId, link.RecordId, result.Verdict, result.Compared,
                string.Join(' ', result.Distances.Select(d => d.ToString(CultureInfo.InvariantCulture))), result.Dropped,
                demoted ? "moved to review" : "kept");
        }
        return (result, incomplete);
    }

    /// <summary>
    /// The local cover images of one volume archive: its stored page-1 thumbnail, and for a spread page 1 both stored crop halves
    /// (a spread with no crop on disk is not compared - the whole jacket is not one cover). <c>missed</c>: a file exists but the
    /// worker gave no hash (busy) - the link is checked again next time.
    /// </summary>
    private async Task<(List<ulong> Hashes, bool Missed)> LocalHashesAsync(VolumeCoverPass.HeldArchive archive, bool spread, CancellationToken ct)
    {
        var hashes = new List<ulong>();
        var missed = false;
        if (spread)
        {
            foreach (var side in new[] { CoverCropSide.Left, CoverCropSide.Right })
            {
                if (state.TryGetCrop(archive.Id, archive.ContentVersion, side, out var known))
                {
                    hashes.Add(known);
                    continue;
                }
                var path = files.CropPath(archive.Id, archive.ContentVersion, side);
                if (!File.Exists(path))
                    continue;
                if (await hasher.HashFileAsync(path, ct) is { } h)
                {
                    state.SetCrop(archive.Id, archive.ContentVersion, side, h);
                    hashes.Add(h);
                }
                else
                {
                    missed = true;
                }
            }
            if (hashes.Count == 0)
                return (hashes, missed);
        }

        if (hashCache.TryGet(archive.Id, archive.ContentVersion, out var cached))
        {
            if (cached is { } c)
                hashes.Add(c);
            return (hashes, missed);
        }
        var thumbnail = thumbnails.GetThumbnailPath(archive.Id, archive.ContentVersion);
        if (!File.Exists(thumbnail))
            return (hashes, missed);
        var hash = await hasher.HashFileAsync(thumbnail, ct);
        if (hash is { } t)
        {
            hashCache.Set(archive.Id, archive.ContentVersion, t);
            hashes.Add(t);
        }
        else
        {
            missed = true;
        }
        return (hashes, missed);
    }

    private async Task<string> PreferredLanguageAsync(CancellationToken ct)
    {
        var language = await db.AppSettings.AsNoTracking().Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => s.MetadataCoverLanguage).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(language) ? "en" : language;
    }

    /// <summary>
    /// The link (a new automatic link to the same record is checked again), the preferred language, the stored web covers and the
    /// local images the check would read; a change means a new check.
    /// </summary>
    private string InputsKey(LinkRow link, string preferred, IEnumerable<WebCover> web, IEnumerable<VolumeCoverPass.HeldArchive> archives, IReadOnlyDictionary<long, bool> spreads)
    {
        var parts = new List<string> { string.Create(CultureInfo.InvariantCulture, $"l{link.LinkedAt.UtcTicks}"), "p" + preferred };
        parts.AddRange(web.Select(c => string.Create(CultureInfo.InvariantCulture, $"w{c.Id}.{c.StoredVersion}")));
        foreach (var a in archives.OrderBy(a => a.Id))
        {
            var thumb = File.Exists(thumbnails.GetThumbnailPath(a.Id, a.ContentVersion)) ? 1 : 0;
            var crops = spreads[a.Id]
                ? (File.Exists(files.CropPath(a.Id, a.ContentVersion, CoverCropSide.Left)) ? 1 : 0) + (File.Exists(files.CropPath(a.Id, a.ContentVersion, CoverCropSide.Right)) ? 2 : 0)
                : 0;
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"a{a.Id}.{a.ContentVersion}.{thumb}.{crops}"));
        }
        return string.Join(',', parts);
    }

    /// <summary>
    /// The same shape as the matcher's review outcome (and the reach check's): no record, the record kept as the one candidate with
    /// the chip, the queue row in Needs review. False when the link changed meanwhile (confirmed, unlinked, another record).
    /// </summary>
    private async Task<bool> DemoteAsync(LinkRow row, CancellationToken ct)
    {
        var link = await db.NodeSeriesLinks.FirstOrDefaultAsync(l => l.NodeId == row.NodeId, ct);
        if (link is null || link.State != (int)SeriesLinkState.Auto || link.RecordId != row.RecordId)
            return false;
        var record = await db.MetadataRecords.FirstOrDefaultAsync(r => r.Id == row.RecordId, ct);
        if (record is null)
            return false;
        var queue = await db.MetadataMatchQueue.FirstOrDefaultAsync(q => q.NodeId == row.NodeId, ct);
        var now = time.GetUtcNow();
        var score = Math.Round(Math.Clamp(link.MatchScore ?? 0, 0, 1), 4);
        link.State = (int)SeriesLinkState.NeedsReview;
        link.RecordId = null;
        link.UpdatedAt = now;
        db.MetadataMatchCandidates.RemoveRange(db.MetadataMatchCandidates.Where(c => c.NodeId == row.NodeId));
        db.MetadataMatchCandidates.Add(new MetadataMatchCandidateEntity
        {
            NodeId = row.NodeId,
            Rank = 1,
            Provider = record.Provider,
            ExternalId = record.ExternalId,
            Title = record.Title.Length <= 512 ? record.Title : record.Title[..512],
            ProviderType = record.ProviderType,
            Format = record.Format,
            Origin = record.Origin,
            Year = record.StartYear,
            Volumes = record.OriginVolumes,
            TitleScore = score,
            AdjustedScore = score,
            Reasons = (int)MatchReason.CoverDiffers,
            ImageRemoteUrl = record.ImageRemoteUrl,
            CreatedAt = now,
        });
        if (queue is not null)
        {
            queue.OutcomeReasons |= (int)MatchReason.CoverDiffers;
            queue.Outcome = (int)MatchBand.NeedsReview;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }
}
