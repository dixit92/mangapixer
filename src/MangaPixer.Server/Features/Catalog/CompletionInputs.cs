namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Features.VolumeScan;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// 1.46.0 lane C: the stamp of everything a folder level's Completion answers are computed from (<c>SeriesProgressLoader</c> over the
/// level's linked series). The answers do not depend on the viewer, so a level's answers are kept while this stamp is unchanged and the
/// next first page reuses them instead of recomputing (~490 ms on the largest live level). The inputs, and what moves each one:
/// <list type="number">
/// <item>The library's <c>CatalogRevision</c> - the series scopes' catalog rows (names, sort keys, kinds, tombstones) and the archives'
/// content versions. Only a scan or a move writes those rows, and both raise the revision after writing.</item>
/// <item><see cref="VolumeContentReader.Generation"/> - the volume contents scan's stored answers (raised after every store / forget).</item>
/// <item>The level's linked series (folder -> record, Confirmed / Auto) - exact.</item>
/// <item>The folders of the library with a link row of any state - a unit subfolder with its own link leaves its series' scope.</item>
/// <item>The declared volumes / edition / tracking facts of the linked folders - exact rows.</item>
/// <item>The preferred language - exact.</item>
/// <item>The linked records' fields the progress reads - exact.</item>
/// <item>The linked records' volume maps: id, source, content hash and version (the hash covers every column the progress reads).</item>
/// <item>The ComicInfo volume / number of the archives in the scopes - exact rows, read again over the scope folders of the last load
/// (<see cref="ComicInfo"/>).</item>
/// </list>
/// Stored rows only; never contacts a provider.
/// </summary>
internal static class CompletionInputs
{
    /// <summary>The stamp of inputs 1-8 (checked on every first page before the answers are reused).</summary>
    public static async Task<string> BaseAsync(MangaPixerDbContext db, long libraryId, IReadOnlyDictionary<long, long> linked, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(linked);
        var stamp = new Stamp();

        stamp.Add("revision").Add(await db.Libraries.AsNoTracking().Where(l => l.Id == libraryId).Select(l => (long?)l.CatalogRevision)
            .FirstOrDefaultAsync(ct));
        stamp.Add("volume-contents").Add(VolumeContentReader.Generation);

        stamp.Add("linked");
        foreach (var (node, record) in linked.OrderBy(l => l.Key))
            stamp.Add(node).Add(record);

        var archive = (int)CatalogNodeKind.Archive;
        var linkRows = await (
            from l in db.NodeSeriesLinks.AsNoTracking()
            join n in db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
            where n.LibraryId == libraryId && n.Kind != archive
            select l.NodeId).ToListAsync(ct);
        linkRows.Sort();
        stamp.Add("link-rows");
        foreach (var node in linkRows)
            stamp.Add(node);

        var nodeIds = linked.Keys.ToList();
        var facts = await db.DeclaredFacts.AsNoTracking()
            .Where(f => f.NodeId != null && nodeIds.Contains(f.NodeId.Value)
                && (f.Key == DeclaredFactKeys.Volumes || f.Key == DeclaredFactKeys.Edition || f.Key == DeclaredFactKeys.Tracking))
            .OrderBy(f => f.Id)
            .Select(f => new { f.Id, NodeId = f.NodeId!.Value, f.Key, f.Value })
            .ToListAsync(ct);
        stamp.Add("declared");
        foreach (var f in facts)
            stamp.Add(f.Id).Add(f.NodeId).Add(f.Key).Add(f.Value);

        stamp.Add("language").Add(await ReleasedInLanguage.PreferredAsync(db, ct));

        var recordIds = linked.Values.Distinct().ToList();
        var records = await db.MetadataRecords.AsNoTracking().Where(r => recordIds.Contains(r.Id))
            .OrderBy(r => r.Id)
            .Select(r => new
            {
                r.Id,
                r.Origin,
                r.OriginStatus,
                r.OriginVolumes,
                r.StatusText,
                r.LatestChapter,
                r.PublishersJson,
                r.LicensedEn,
                r.TranslationComplete,
                r.Webtoon,
            })
            .ToListAsync(ct);
        stamp.Add("records");
        foreach (var r in records)
        {
            stamp.Add(r.Id).Add(r.Origin).Add(r.OriginStatus).Add(r.OriginVolumes).Add(r.StatusText).Add(r.LatestChapter).Add(r.PublishersJson)
                .Add(r.LicensedEn).Add(r.TranslationComplete).Add(r.Webtoon);
        }

        var maps = await db.SeriesVolumeMaps.AsNoTracking().Where(m => recordIds.Contains(m.RecordId))
            .OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.RecordId, m.Source, m.ContentHash, m.Version })
            .ToListAsync(ct);
        stamp.Add("maps");
        foreach (var m in maps)
            stamp.Add(m.Id).Add(m.RecordId).Add(m.Source).Add(m.ContentHash).Add(m.Version);

        return stamp.Finish();
    }

    /// <summary>The stamp of input 9: the ComicInfo rows a load read (or reads again) below the scope folders.</summary>
    public static string ComicInfo(IReadOnlyDictionary<long, (int? Volume, string? Number)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var stamp = new Stamp().Add("comicinfo");
        foreach (var (node, (volume, number)) in rows.OrderBy(r => r.Key))
            stamp.Add(node).Add(volume).Add(number);
        return stamp.Finish();
    }

    /// <summary>A SHA-256 over typed, length-prefixed values (no two different value lists give the same bytes).</summary>
    private sealed class Stamp
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _buffer = new byte[9];

        private Stamp Write(byte tag, long value)
        {
            _buffer[0] = tag;
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(1), value);
            _hash.AppendData(_buffer);
            return this;
        }

        private Stamp Null() => Write(0, 0);

        public Stamp Add(long value) => Write(1, value);

        public Stamp Add(int value) => Write(1, value);

        public Stamp Add(long? value) => value is { } v ? Add(v) : Null();

        public Stamp Add(int? value) => value is { } v ? Add((long)v) : Null();

        public Stamp Add(bool? value) => value is { } v ? Write(2, v ? 1 : 0) : Null();

        public Stamp Add(double? value) => value is { } v ? Write(3, BitConverter.DoubleToInt64Bits(v)) : Null();

        public Stamp Add(string? value)
        {
            if (value is null)
                return Null();
            var bytes = Encoding.UTF8.GetBytes(value);
            Write(4, bytes.Length);
            _hash.AppendData(bytes);
            return this;
        }

        public string Finish()
        {
            var text = Convert.ToHexString(_hash.GetHashAndReset());
            _hash.Dispose();
            return text;
        }
    }
}
