namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Creators;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Index;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>The outcome of an admin creator action: the creator to show, or an error code.</summary>
public sealed record CreatorAdminResult(CreatorDto? Creator, string? Error = null)
{
    public static CreatorAdminResult Fail(string error) => new(null, error);
}

/// <summary>
/// Admin decisions about canonical creators (1.45.0 lane F, D1 2.2): merge one creator into another, split chosen provider references /
/// spellings off a creator into a new one, set or clear the display name. Each decision is stored as an override (<c>creator_overrides</c> -
/// admin input, never derived, never purged), the creator rows follow at once, the merge / split are audited (<c>metadata.creator_merge</c> /
/// <c>metadata.creator_split</c>, public creator ids only) and every library's index is nudged (creator values and ComicInfo work keys
/// re-key on the next pass - owner Q5). A merged creator's id answers with its target (<see cref="CreatorDto.RedirectedFrom"/>).
/// Override subjects: a merge and a display name name the creator's public id; a split pin names a provider reference or a name key.
/// </summary>
public sealed class CreatorAdminService
{
    public const int MaxSearchResults = 50;

    private readonly MangaPixerDbContext _db;
    private readonly AuditService _audit;
    private readonly IFacetIndexNudge _nudge;
    private readonly CreatorStateCache _cache;
    private readonly FacetIndexLock _lock;
    private readonly TimeProvider _time;
    private readonly ILogger<CreatorAdminService> _logger;

    public CreatorAdminService(MangaPixerDbContext db, AuditService audit, IFacetIndexNudge nudge, CreatorStateCache cache, FacetIndexLock gate,
        TimeProvider time, ILogger<CreatorAdminService> logger)
    {
        _db = db;
        _audit = audit;
        _nudge = nudge;
        _cache = cache;
        _lock = gate;
        _time = time;
        _logger = logger;
    }

    /// <summary>The creator of a public id (a merged one redirects to its target), or null when unknown.</summary>
    public async Task<CreatorDto?> GetAsync(string publicId, CancellationToken ct)
    {
        if (!FacetPublicIds.IsCreatorId(publicId))
            return null;
        var asked = await _db.Creators.AsNoTracking().FirstOrDefaultAsync(c => c.PublicId == publicId, ct);
        if (asked is null)
            return null;
        var live = await FinalAsync(asked, ct);
        var dto = await DtoAsync(live, ct);
        return live.Id == asked.Id ? dto : dto with { RedirectedFrom = asked.PublicId };
    }

    /// <summary>Live creators whose spellings contain the query's name key (or all, newest first, without a query).</summary>
    public async Task<CreatorSearchResultDto> SearchAsync(string? query, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxSearchResults);
        var key = string.IsNullOrWhiteSpace(query) ? string.Empty : DeclaredFactsComparer.NameKey(query);
        var live = _db.Creators.AsNoTracking().Where(c => c.MergedIntoId == null);
        if (key.Length > 0)
            live = live.Where(c => _db.CreatorNames.Any(n => n.CreatorId == c.Id && n.NameKey.Contains(key)));
        var rows = await live.OrderBy(c => c.DisplayName).ThenBy(c => c.Id).Take(limit).ToListAsync(ct);
        var items = new List<CreatorDto>(rows.Count);
        foreach (var row in rows)
            items.Add(await DtoAsync(row, ct));
        return new CreatorSearchResultDto { Items = items };
    }

    /// <summary>Merges the creator of <paramref name="publicId"/> into <paramref name="intoPublicId"/>; errors: not_found, invalid, same_creator.</summary>
    public async Task<CreatorAdminResult> MergeAsync(string publicId, string? intoPublicId, string? actor, CancellationToken ct)
    {
        if (!FacetPublicIds.IsCreatorId(intoPublicId))
            return CreatorAdminResult.Fail("invalid");
        await _lock.Gate.WaitAsync(ct);
        try
        {
            var source = await _db.Creators.FirstOrDefaultAsync(c => c.PublicId == publicId, ct);
            var target = await _db.Creators.FirstOrDefaultAsync(c => c.PublicId == intoPublicId, ct);
            if (source is null || target is null)
                return CreatorAdminResult.Fail("not_found");
            var from = await FinalAsync(source, ct);
            var into = await FinalAsync(target, ct);
            if (from.Id == into.Id)
                return CreatorAdminResult.Fail("same_creator");

            var now = _time.GetUtcNow();
            var actorId = await ActorIdAsync(actor, ct);
            _db.CreatorOverrides.Add(new CreatorOverrideEntity
            {
                Kind = (int)CreatorOverrideKind.Merge,
                Subject = from.PublicId,
                TargetCreatorId = into.Id,
                ActorUserId = actorId,
                CreatedAt = now,
            });
            var tracked = await _db.Creators.FirstAsync(c => c.Id == from.Id, ct);
            tracked.MergedIntoId = into.Id;
            tracked.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            // Creators merged into the source earlier now point at the target directly (short redirect chains).
            await _db.Creators.Where(c => c.MergedIntoId == from.Id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.MergedIntoId, into.Id)
                .SetProperty(c => c.UpdatedAt, now), ct);
            await _audit.RecordAsync(AuditActions.MetadataCreatorMerge, AuditResults.Success, actor,
                correlationId: from.PublicId + ">" + into.PublicId, ct: ct);
            _logger.LogInformation(LogEvents.Metadata.FacetCreatorMerged, "Creator {CreatorId} merged into {TargetId}", from.Id, into.Id);
        }
        finally
        {
            _lock.Gate.Release();
        }
        Changed();
        return new CreatorAdminResult(await GetAsync(intoPublicId!, ct));
    }

    /// <summary>
    /// Splits chosen provider references and spellings off a creator into a new one (split pins keep the rules from undoing it). Errors:
    /// not_found, empty_selection, not_on_creator, split_all.
    /// </summary>
    public async Task<CreatorAdminResult> SplitAsync(string publicId, IReadOnlyList<string>? providerIds, IReadOnlyList<string>? names,
        string? actor, CancellationToken ct)
    {
        var refs = (providerIds ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var spellings = (names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        if (refs.Count == 0 && spellings.Count == 0)
            return CreatorAdminResult.Fail("empty_selection");
        string newPublicId;
        await _lock.Gate.WaitAsync(ct);
        try
        {
            var asked = await _db.Creators.AsNoTracking().FirstOrDefaultAsync(c => c.PublicId == publicId, ct);
            if (asked is null)
                return CreatorAdminResult.Fail("not_found");
            var creator = await FinalAsync(asked, ct);
            var held = await _db.CreatorProviderIds.Where(p => p.CreatorId == creator.Id).ToListAsync(ct);
            var heldNames = await _db.CreatorNames.Where(n => n.CreatorId == creator.Id).ToListAsync(ct);
            var movingRefs = held.Where(p => refs.Contains(p.Provider + ":" + p.ExternalId, StringComparer.Ordinal)).ToList();
            var movingNames = heldNames.Where(n => spellings.Contains(n.Name, StringComparer.Ordinal)).ToList();
            if (movingRefs.Count != refs.Count || movingNames.Count != spellings.Count)
                return CreatorAdminResult.Fail("not_on_creator");
            // Keys of the moved spellings move whole (every spelling with that key), so a pin on the key is the split.
            var keys = movingNames.Select(n => n.NameKey).ToHashSet(StringComparer.Ordinal);
            movingNames = heldNames.Where(n => keys.Contains(n.NameKey)).ToList();
            if (movingRefs.Count == held.Count && movingNames.Count == heldNames.Count)
                return CreatorAdminResult.Fail("split_all");

            var now = _time.GetUtcNow();
            var actorId = await ActorIdAsync(actor, ct);
            var existingIds = (await _db.Creators.AsNoTracking().Select(c => c.PublicId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
            do
            {
                newPublicId = FacetPublicIds.NewCreatorId();
            }
            while (existingIds.Contains(newPublicId));
            var display = movingNames.Select(n => n.Name).Order(StringComparer.Ordinal).FirstOrDefault()
                ?? movingRefs.Select(p => p.Provider + ":" + p.ExternalId).First();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var split = new CreatorEntity
            {
                PublicId = newPublicId,
                DisplayName = display,
                DisplayNameSource = (int)CreatorDisplayNameSource.MostUsed,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Creators.Add(split);
            await _db.SaveChangesAsync(ct);
            foreach (var p in movingRefs)
            {
                p.CreatorId = split.Id;
                _db.CreatorOverrides.Add(Pin(p.Provider + ":" + p.ExternalId, split.Id, actorId, now));
            }
            foreach (var n in movingNames)
                n.CreatorId = split.Id;
            foreach (var key in keys)
                _db.CreatorOverrides.Add(Pin(key, split.Id, actorId, now));
            var tracked = await _db.Creators.FirstAsync(c => c.Id == creator.Id, ct);
            tracked.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await _audit.RecordAsync(AuditActions.MetadataCreatorSplit, AuditResults.Success, actor,
                correlationId: creator.PublicId + ">" + newPublicId, ct: ct);
            _logger.LogInformation(LogEvents.Metadata.FacetCreatorSplit, "Creator {CreatorId} split: {Refs} provider id(s), {Names} spelling(s) to {NewId}",
                creator.Id, movingRefs.Count, movingNames.Count, split.Id);
        }
        finally
        {
            _lock.Gate.Release();
        }
        Changed();
        return new CreatorAdminResult(await GetAsync(newPublicId, ct));
    }

    /// <summary>Sets (or, with a blank name, clears) the admin display name; errors: not_found, invalid.</summary>
    public async Task<CreatorAdminResult> SetDisplayNameAsync(string publicId, string? displayName, string? actor, CancellationToken ct)
    {
        var name = FacetText.Display(displayName);
        if (displayName is not null && displayName.Length > Persistence.FacetModel.NameLength)
            return CreatorAdminResult.Fail("invalid");
        await _lock.Gate.WaitAsync(ct);
        try
        {
            var asked = await _db.Creators.AsNoTracking().FirstOrDefaultAsync(c => c.PublicId == publicId, ct);
            if (asked is null)
                return CreatorAdminResult.Fail("not_found");
            var creator = await FinalAsync(asked, ct);
            var now = _time.GetUtcNow();
            if (name is null)
            {
                await _db.CreatorOverrides.Where(o => o.Kind == (int)CreatorOverrideKind.DisplayName && o.TargetCreatorId == creator.Id)
                    .ExecuteDeleteAsync(ct);
            }
            else
            {
                _db.CreatorOverrides.Add(new CreatorOverrideEntity
                {
                    Kind = (int)CreatorOverrideKind.DisplayName,
                    Subject = creator.PublicId,
                    TargetCreatorId = creator.Id,
                    Value = name,
                    ActorUserId = await ActorIdAsync(actor, ct),
                    CreatedAt = now,
                });
                var tracked = await _db.Creators.FirstAsync(c => c.Id == creator.Id, ct);
                tracked.DisplayName = name;
                tracked.DisplayNameSource = (int)CreatorDisplayNameSource.Admin;
                tracked.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);
                await _db.FacetValues.Where(v => v.CreatorId == creator.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.Display, name), ct);
            }
            _logger.LogInformation(LogEvents.Metadata.FacetCreatorRenamed, "Creator {CreatorId} display name {Change}", creator.Id, name is null ? "cleared" : "set");
            publicId = creator.PublicId;
        }
        finally
        {
            _lock.Gate.Release();
        }
        Changed();
        return new CreatorAdminResult(await GetAsync(publicId, ct));
    }

    private void Changed()
    {
        _cache.Invalidate();
        _nudge.AllChanged(FacetNudgeReason.CreatorOverrideChanged);
    }

    private static CreatorOverrideEntity Pin(string subject, long target, long? actorId, DateTimeOffset now) => new()
    {
        Kind = (int)CreatorOverrideKind.SplitPin,
        Subject = subject.Length > Persistence.FacetModel.SubjectLength ? subject[..Persistence.FacetModel.SubjectLength] : subject,
        TargetCreatorId = target,
        ActorUserId = actorId,
        CreatedAt = now,
    };

    private async Task<long?> ActorIdAsync(string? actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actor))
            return null;
        var normalized = actor.ToUpperInvariant();
        return await _db.Users.AsNoTracking().Where(u => u.NormalizedUserName == normalized).Select(u => (long?)u.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>The live creator a creator redirects to (merges followed, bounded).</summary>
    private async Task<CreatorEntity> FinalAsync(CreatorEntity creator, CancellationToken ct)
    {
        var current = creator;
        for (var hop = 0; hop < CreatorResolver.MaxMergeHops && current.MergedIntoId is { } next && next != current.Id; hop++)
        {
            var target = await _db.Creators.AsNoTracking().FirstOrDefaultAsync(c => c.Id == next, ct);
            if (target is null)
                break;
            current = target;
        }
        return current;
    }

    private async Task<CreatorDto> DtoAsync(CreatorEntity creator, CancellationToken ct)
    {
        var names = await _db.CreatorNames.AsNoTracking().Where(n => n.CreatorId == creator.Id)
            .Select(n => new { n.Name, n.Source, n.Script }).ToListAsync(ct);
        var refs = await _db.CreatorProviderIds.AsNoTracking().Where(p => p.CreatorId == creator.Id)
            .Select(p => p.Provider + ":" + p.ExternalId).ToListAsync(ct);
        var mergedFrom = await _db.Creators.AsNoTracking().Where(c => c.MergedIntoId == creator.Id).Select(c => c.PublicId).ToListAsync(ct);
        var value = await _db.FacetValues.AsNoTracking()
            .Where(v => v.Bucket == (int)FacetBucket.Creators && v.CreatorId == creator.Id)
            .Select(v => new { v.Id, v.PublicId })
            .FirstOrDefaultAsync(ct);
        var works = value is null ? 0 : await _db.FacetEntries.AsNoTracking().Where(e => e.ValueId == value.Id).Select(e => e.WorkId).Distinct().CountAsync(ct);
        return new CreatorDto
        {
            Id = creator.PublicId,
            DisplayName = creator.DisplayName,
            DisplayNameSource = ((CreatorDisplayNameSource)creator.DisplayNameSource) switch
            {
                CreatorDisplayNameSource.Provider => "provider",
                CreatorDisplayNameSource.Admin => "admin",
                _ => "mostUsed",
            },
            Names = names
                .OrderBy(n => n.Name, StringComparer.Ordinal)
                .Select(n => new CreatorNameDto
                {
                    Name = n.Name,
                    Source = ((CreatorNameSource)n.Source) switch
                    {
                        CreatorNameSource.Record => "record",
                        CreatorNameSource.ProviderAuthor => "providerAuthor",
                        CreatorNameSource.ComicInfo => "comicInfo",
                        CreatorNameSource.Declared => "declared",
                        _ => "admin",
                    },
                    Script = ((CreatorNameScript)n.Script) switch
                    {
                        CreatorNameScript.Latin => "latin",
                        CreatorNameScript.Cjk => "cjk",
                        CreatorNameScript.Hangul => "hangul",
                        _ => "other",
                    },
                })
                .ToList(),
            ProviderIds = refs.Order(StringComparer.Ordinal).ToList(),
            MergedFrom = mergedFrom.Order(StringComparer.Ordinal).ToList(),
            ValueId = value?.PublicId,
            Works = works,
        };
    }
}
