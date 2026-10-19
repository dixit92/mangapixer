namespace com.lifepixer.mangapixer.Core.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>
/// One place a work lives (D1 2.1): a folder or an archive of the library. <paramref name="ArchiveIds"/> are the archives whose ComicInfo
/// belongs to the work HERE (the location itself for an archive; the folder's archives and those of its covered subfolders, minus every
/// archive with a decision of its own). <paramref name="AboutRecordId"/>: the record of the nearest "Collection about" folder the location
/// sits in (the About bucket, owner Q8), or null.
/// </summary>
public sealed record FacetWorkLocation(long NodeId, IReadOnlyList<long> ArchiveIds, long? AboutRecordId);

/// <summary>A work of one library: its stable key (<see cref="FacetWorkKey"/>), kind, linked record, title and locations.</summary>
public sealed record FacetResolvedWork(string Key, FacetWorkKind Kind, long? RecordId, string Title, IReadOnlyList<FacetWorkLocation> Locations);

/// <summary>A ComicInfo work of ANOTHER library: its key's series form and resolved creator ids (a candidate to share the key with).</summary>
public sealed record FacetComicInfoWorkKey(string Key, string SeriesForm, IReadOnlyCollection<long> CreatorIds);

/// <summary>What <see cref="WorkResolver.Resolve"/> reads.</summary>
/// <param name="Tree">The library's live tree, link rows and declared facts.</param>
/// <param name="Classify">The work detector's verdict for a folder (the server passes the matcher's own detector over the tree snapshot).</param>
/// <param name="ComicInfo">The current parsed ComicInfo by archive id.</param>
/// <param name="RecordTitles">The title of each linked record.</param>
/// <param name="ComicInfoCreators">The RESOLVED creator ids (writer / artist credits, after the creator rules and overrides) of an
/// archive's ComicInfo - owner Q5: the <c>ci:</c> key's creator part.</param>
/// <param name="OtherLibraries">ComicInfo works other libraries already hold (Q5 across libraries: the same series and a same creator
/// share one key).</param>
public sealed record WorkResolverInput(
    FacetTree Tree,
    Func<long, WorkClassification> Classify,
    IReadOnlyDictionary<long, FacetComicInfo> ComicInfo,
    IReadOnlyDictionary<long, string> RecordTitles,
    Func<long, IReadOnlyCollection<long>> ComicInfoCreators,
    IReadOnlyList<FacetComicInfoWorkKey>? OtherLibraries = null);

/// <summary>
/// Work identity (1.45.0 lane F, D1 2.1 rules 1-6 with the owner answers of 2026-10-19). Pure: the tree, the link rows, the current ComicInfo,
/// the detector's classes and the resolved ComicInfo creators are its only inputs. Walks top-down; the nearest decision wins.
/// <list type="number">
/// <item><b>Linked series</b> (<see cref="SeriesLinkStates.IsSeries"/>) on a node -&gt; <c>rec:&lt;record&gt;</c>, located at the node; everything
/// below belongs to it except a node with a decision of its own. The same record linked from several folders or libraries is ONE work
/// (Q6); a node below linked to the same record adds no location.</item>
/// <item><b>Collection about / Artist folder</b> (<see cref="SeriesLinkStates.CoverBelow"/> = Opens): the folder is not a work; its archives
/// (the detector's archive groups) and subfolders are works of their own; Collection about gives them the About value.</item>
/// <item><b>Don't match / Needs review / no row</b>: no record applies; the detector's class decides - a folder-level class (Series,
/// SeriesWithUnits, OneShot, and the review-only Ambiguous / Mixed) is one work; an archive-level class (collections, artist folders,
/// loose works next to subfolders) makes each archive group a work; containers, wrappers and unit folders are never works. Key
/// <c>ci:&lt;series form&gt;#&lt;resolved creator ids&gt;</c> when the work's ComicInfo names one series and at least one creator - works with the
/// same series sharing a creator are ONE work (Q5) - else <c>node:&lt;anchor&gt;</c>.</item>
/// <item><b>A folder holding two series</b> (unlinked; its ComicInfo series below the 0.6 majority): split per archive by ComicInfo series;
/// archives without one form the remainder work <c>node:&lt;folder&gt;</c>. A LINKED folder stays one work.</item>
/// <item><b>A one-shot archive</b> with its own decision (a link, Don't match) is its own work.</item>
/// <item>Franchises (main + spin-off) are not merged.</item>
/// </list>
/// Each node is the location of at most one work, so an entry (value, location) is never claimed twice.
/// </summary>
public static class WorkResolver
{
    /// <summary>The ComicInfo series share at or above which a work names ONE series (the series-information resolver's majority).</summary>
    public const double MajorityShare = 0.6;

    private sealed class Location(long nodeId, long? about)
    {
        public long NodeId { get; } = nodeId;
        public long? About { get; } = about;
        public List<long> Archives { get; } = [];
    }

    private sealed class Work
    {
        public long? RecordId { get; init; }
        public long AnchorNodeId { get; init; }
        public string Title { get; init; } = string.Empty;
        public List<Location> Locations { get; } = [];
        public bool SplitByComicInfo { get; init; }
    }

    private readonly record struct Context(Location? Current, Work? CurrentWork, bool CoversSubtree, long? About);

    public static IReadOnlyList<FacetResolvedWork> Resolve(WorkResolverInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tree = input.Tree;
        var recordWorks = new Dictionary<long, Work>();
        var unlinked = new List<Work>();

        Work RecordWork(long recordId, long anchor)
        {
            if (!recordWorks.TryGetValue(recordId, out var work))
            {
                recordWorks[recordId] = work = new Work
                {
                    RecordId = recordId,
                    AnchorNodeId = anchor,
                    Title = input.RecordTitles.TryGetValue(recordId, out var t) ? t : tree.Find(anchor)?.Name ?? string.Empty,
                };
            }
            return work;
        }

        Location NewLocation(Work work, long nodeId, long? about)
        {
            var location = new Location(nodeId, about);
            work.Locations.Add(location);
            return location;
        }

        var stack = new Stack<(FacetNode Folder, Context Ctx)>();
        foreach (var root in tree.Roots.Where(r => r.IsFolder).Reverse())
            stack.Push((root, new Context(null, null, false, null)));
        // Loose archives at the top (no folder): each is its own work only with a decision of its own; otherwise not a work.
        foreach (var root in tree.Roots.Where(r => !r.IsFolder))
            OwnDecisionArchive(root, default);

        while (stack.Count > 0)
        {
            var (folder, ctx) = stack.Pop();
            var own = tree.OwnStoppingLink(folder.Id);
            if (own is not null && SeriesLinkStates.IsSeries(own.State) && own.RecordId is { } recordId)
            {
                if (ctx.CurrentWork?.RecordId == recordId && ctx.CoversSubtree)
                {
                    Covered(folder, ctx); // The same series below its own folder: no second location.
                    continue;
                }
                var work = RecordWork(recordId, folder.Id);
                var location = NewLocation(work, folder.Id, ctx.About);
                Covered(folder, new Context(location, work, true, ctx.About));
                continue;
            }
            if (own is not null && SeriesLinkStates.CoverBelow(own.State) == MatchingCoverKind.Opens)
            {
                var about = own.State == SeriesLinkState.CollectionAbout && own.RecordId is { } aboutRecord ? aboutRecord : ctx.About;
                Fresh(folder, about, opener: true);
                continue;
            }
            if (own is not null)
            {
                Fresh(folder, ctx.About, opener: false); // Don't match: no record here; the detector decides.
                continue;
            }
            if (ctx.Current is not null && ctx.CoversSubtree)
            {
                Covered(folder, ctx);
                continue;
            }
            Fresh(folder, ctx.About, opener: false);
        }

        void Covered(FacetNode folder, Context ctx)
        {
            foreach (var child in tree.ChildrenOf(folder.Id))
            {
                if (child.IsFolder)
                    continue;
                if (!OwnDecisionArchive(child, ctx))
                    ctx.Current!.Archives.Add(child.Id);
            }
            PushFolders(folder, ctx);
        }

        void PushFolders(FacetNode folder, Context ctx)
        {
            var folders = tree.ChildrenOf(folder.Id).Where(c => c.IsFolder).ToList();
            for (var i = folders.Count - 1; i >= 0; i--)
                stack.Push((folders[i], ctx));
        }

        void Fresh(FacetNode folder, long? about, bool opener)
        {
            var classification = input.Classify(folder.Id);
            var children = tree.ChildrenOf(folder.Id);
            var archives = children.Where(c => !c.IsFolder).ToList();
            var fresh = new Context(null, null, false, about);
            var level = classification.Level;
            if (opener && level is MatchLevel.Folder or MatchLevel.ReviewOnly)
                level = MatchLevel.Archive; // An opener is never a work itself (the detector already makes it archive level).

            switch (level)
            {
                case MatchLevel.Folder:
                case MatchLevel.ReviewOnly:
                    {
                        var work = new Work { AnchorNodeId = folder.Id, Title = folder.Name };
                        unlinked.Add(work);
                        var location = NewLocation(work, folder.Id, about);
                        // Mixed (one subfolder + loose units of one work): the loose archives are the work; the subfolder stands on its own.
                        var covers = classification.Class != WorkClass.Mixed;
                        var ctx = new Context(location, work, covers, about);
                        foreach (var archive in archives)
                        {
                            if (!OwnDecisionArchive(archive, ctx))
                                location.Archives.Add(archive.Id);
                        }
                        PushFolders(folder, covers ? ctx : fresh);
                        return;
                    }
                case MatchLevel.Archive:
                    {
                        var grouped = new HashSet<long>();
                        var groups = classification.ArchiveGroups.Count > 0 || !opener
                            ? classification.ArchiveGroups
                            : archives.Select((_, i) => new ArchiveGroup(string.Empty, [i])).ToList();
                        foreach (var group in groups)
                        {
                            var members = group.ArchiveIndexes
                                .Where(i => i >= 0 && i < archives.Count)
                                .Distinct()
                                .Order()
                                .Select(i => archives[i])
                                .Where(a => tree.OwnStoppingLink(a.Id) is null && grouped.Add(a.Id))
                                .ToList();
                            if (members.Count == 0)
                                continue;
                            var title = members.Count == 1 || group.QueryTitle.Length == 0 ? members[0].Name : group.QueryTitle;
                            var work = new Work { AnchorNodeId = members[0].Id, Title = title };
                            unlinked.Add(work);
                            foreach (var member in members)
                                NewLocation(work, member.Id, about).Archives.Add(member.Id);
                        }
                        foreach (var archive in archives)
                            OwnDecisionArchive(archive, fresh);
                        PushFolders(folder, fresh);
                        return;
                    }
                default:
                    foreach (var archive in archives)
                        OwnDecisionArchive(archive, fresh);
                    PushFolders(folder, fresh);
                    return;
            }
        }

        // An archive with a decision of its own: a link to the series it already belongs to adds nothing; another link makes it a location
        // of that record's work; Don't match (or an opener row on an archive) makes it its own unlinked work. Returns false when the archive
        // has no decision of its own (the caller places it).
        bool OwnDecisionArchive(FacetNode archive, Context ctx)
        {
            var own = tree.OwnStoppingLink(archive.Id);
            if (own is null)
                return false;
            if (SeriesLinkStates.IsSeries(own.State) && own.RecordId is { } recordId)
            {
                if (ctx.CurrentWork?.RecordId == recordId && ctx.Current is not null)
                {
                    ctx.Current.Archives.Add(archive.Id);
                    return true;
                }
                NewLocation(RecordWork(recordId, archive.Id), archive.Id, ctx.About).Archives.Add(archive.Id);
                return true;
            }
            var work = new Work { AnchorNodeId = archive.Id, Title = archive.Name };
            unlinked.Add(work);
            NewLocation(work, archive.Id, ctx.About).Archives.Add(archive.Id);
            return true;
        }

        // Rule 4: an unlinked folder work whose ComicInfo names several series below the majority is split per archive.
        var split = new List<Work>();
        foreach (var work in unlinked)
        {
            if (work.Locations.Count != 1 || tree.Find(work.AnchorNodeId) is not { IsFolder: true })
            {
                split.Add(work);
                continue;
            }
            var location = work.Locations[0];
            var bySeries = location.Archives
                .Select(a => (Archive: a, Form: FacetText.SeriesForm(input.ComicInfo.GetValueOrDefault(a)?.Series)))
                .ToList();
            var named = bySeries.Where(x => x.Form is not null).ToList();
            var top = named.GroupBy(x => x.Form!, StringComparer.Ordinal).Select(g => g.Count()).DefaultIfEmpty(0).Max();
            if (named.Count < 2 || top >= MajorityShare * named.Count)
            {
                split.Add(work);
                continue;
            }
            foreach (var group in named.GroupBy(x => x.Form!, StringComparer.Ordinal).OrderBy(g => g.Min(x => x.Archive)))
            {
                var ids = group.Select(x => x.Archive).Order().ToList();
                var part = new Work
                {
                    AnchorNodeId = ids[0],
                    Title = SeriesSpelling(ids, input) ?? tree.Find(ids[0])?.Name ?? string.Empty,
                    SplitByComicInfo = true,
                };
                foreach (var id in ids)
                    part.Locations.Add(new Location(id, location.About) { Archives = { id } });
                split.Add(part);
            }
            var rest = bySeries.Where(x => x.Form is null).Select(x => x.Archive).ToList();
            if (rest.Count > 0)
            {
                var remainder = new Work { AnchorNodeId = work.AnchorNodeId, Title = work.Title };
                var remainderLocation = new Location(location.NodeId, location.About);
                remainderLocation.Archives.AddRange(rest);
                remainder.Locations.Add(remainderLocation);
                split.Add(remainder);
            }
        }

        var results = new List<FacetResolvedWork>(recordWorks.Count + split.Count);
        foreach (var work in recordWorks.Values.OrderBy(w => w.RecordId))
            results.Add(Build(FacetWorkKey.Record(work.RecordId!.Value), FacetWorkKind.Record, work.RecordId, work.Title, work.Locations));

        // Unlinked: a ComicInfo key when the work names one series and at least one resolved creator; works of one series sharing a creator
        // are one work (union-find within the library, then a key another library already holds for the same series and a shared creator).
        var candidates = new List<(Work Work, string Form, HashSet<long> Creators, string Spelling)>();
        foreach (var work in split)
        {
            if (ComicInfoIdentity(work, input) is { } identity)
                candidates.Add((work, identity.Form, identity.Creators, identity.Spelling));
            else
                results.Add(Build(FacetWorkKey.Node(work.AnchorNodeId), FacetWorkKind.Node, null, work.Title, work.Locations));
        }

        var parent = Enumerable.Range(0, candidates.Count).ToArray();
        int FindRoot(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }
        foreach (var bucket in Enumerable.Range(0, candidates.Count).GroupBy(i => candidates[i].Form, StringComparer.Ordinal))
        {
            var members = bucket.ToList();
            for (var a = 0; a < members.Count; a++)
            {
                for (var b = a + 1; b < members.Count; b++)
                {
                    if (candidates[members[a]].Creators.Overlaps(candidates[members[b]].Creators))
                        parent[FindRoot(members[b])] = FindRoot(members[a]);
                }
            }
        }

        var others = input.OtherLibraries ?? [];
        var byKey = new Dictionary<string, (string Title, List<Location> Locations, long Anchor)>(StringComparer.Ordinal);
        foreach (var group in Enumerable.Range(0, candidates.Count).GroupBy(FindRoot))
        {
            var members = group.Select(i => candidates[i]).ToList();
            var form = members[0].Form;
            var creators = members.SelectMany(m => m.Creators).ToHashSet();
            var adopted = others
                .Where(o => string.Equals(o.SeriesForm, form, StringComparison.Ordinal) && o.CreatorIds.Any(creators.Contains))
                .Select(o => o.Key)
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
            var key = adopted ?? FacetWorkKey.ComicInfo(form, creators.Order().Take(FacetWorkKey.MaxCreatorIds));
            var locations = members.SelectMany(m => m.Work.Locations).ToList();
            var title = members.Select(m => m.Spelling).GroupBy(s => s, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
            var anchor = members.Min(m => m.Work.AnchorNodeId);
            if (byKey.TryGetValue(key, out var existing))
            {
                existing.Locations.AddRange(locations);
                byKey[key] = (existing.Title, existing.Locations, Math.Min(existing.Anchor, anchor));
            }
            else
            {
                byKey[key] = (title, locations, anchor);
            }
        }
        foreach (var (key, value) in byKey.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            results.Add(Build(key, FacetWorkKind.ComicInfo, null, value.Title, value.Locations));

        return results;
    }

    private static (string Form, HashSet<long> Creators, string Spelling)? ComicInfoIdentity(Work work, WorkResolverInput input)
    {
        var rows = work.Locations.SelectMany(l => l.Archives)
            .Select(a => input.ComicInfo.GetValueOrDefault(a))
            .OfType<FacetComicInfo>()
            .Select(ci => (Row: ci, Form: FacetText.SeriesForm(ci.Series)))
            .Where(x => x.Form is not null)
            .ToList();
        if (rows.Count == 0)
            return null;
        var top = rows.GroupBy(x => x.Form!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First();
        if (top.Count() < MajorityShare * rows.Count)
            return null;
        var creators = top.SelectMany(x => input.ComicInfoCreators(x.Row.ArchiveId)).Where(id => id > 0).ToHashSet();
        if (creators.Count == 0)
            return null;
        var spelling = top.Select(x => FacetText.Display(x.Row.Series)!).GroupBy(s => s, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
        return (top.Key, creators, spelling);
    }

    private static string? SeriesSpelling(IEnumerable<long> archives, WorkResolverInput input) =>
        archives.Select(a => FacetText.Display(input.ComicInfo.GetValueOrDefault(a)?.Series))
            .OfType<string>()
            .GroupBy(s => s, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault();

    private static FacetResolvedWork Build(string key, FacetWorkKind kind, long? recordId, string title, IEnumerable<Location> locations) =>
        new(key, kind, recordId, title,
            locations.OrderBy(l => l.NodeId)
                .Select(l => new FacetWorkLocation(l.NodeId, l.Archives.Distinct().Order().ToList(), l.About))
                .ToList());
}
