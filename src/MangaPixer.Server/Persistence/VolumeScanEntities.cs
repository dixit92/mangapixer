namespace com.lifepixer.mangapixer.Server.Persistence.Entities;

// 1.44.0 volume contents scan (lane V, migration AddVolumeContentScan). Configured in Persistence/VolumeScanModel.cs. No path, no name:
// node ids, content versions, counts and page hashes only. Everything here is derived from the files and rebuilt when they change.

/// <summary>
/// The perceptual page hashes of one archive (one row per hashed archive file): 8 bytes per page in reading order (the stored page
/// ordinals), hashed in chunks - <see cref="HashedPages"/> is the resume point. A changed <see cref="ContentVersion"/> makes the row stale
/// (re-hashed when needed); a removed archive cascades.
/// </summary>
public sealed class ArchivePageHashEntity
{
    /// <summary>The archive's node (= <c>archive_items.NodeId</c>).</summary>
    public long NodeId { get; set; }

    /// <summary>The archive's content version the hashes belong to.</summary>
    public long ContentVersion { get; set; }

    public int PageCount { get; set; }

    /// <summary>Pages hashed so far (0 .. <see cref="PageCount"/>): the next chunk starts here.</summary>
    public int HashedPages { get; set; }

    /// <summary>8 bytes per page (little-endian 64-bit perceptual hash); <see cref="PageCount"/> x 8 bytes.</summary>
    public byte[] Hashes { get; set; } = [];

    /// <summary>One bit per page: set = that page could not be hashed (too large, not decodable).</summary>
    public byte[] Unhashed { get; set; } = [];

    /// <summary>The hasher's version (a change re-hashes): <see cref="Features.VolumeScan.VolumeContentScanService.HashAlgorithm"/>.</summary>
    public int Algorithm { get; set; }

    /// <summary>Null while hashing or when done; an archive-level error code otherwise (encrypted, too_large, ...): not retried until
    /// the file changes.</summary>
    public string? Error { get; set; }

    public DateTimeOffset HashedAt { get; set; }
}

/// <summary>
/// The scan's answer for one volume file (one row per scanned volume file): the state, the chapter files proven inside it
/// (<see cref="ChaptersJson"/>: node id, content version, number, first / last volume page, matched / pages, split) and the candidate set
/// it was computed from (<see cref="CandidatesKey"/>: a new, changed or removed chapter file re-runs the match - cheap, the hashes are
/// stored).
/// </summary>
public sealed class VolumeContentEntity
{
    /// <summary>The volume file's node (= <c>archive_items.NodeId</c>).</summary>
    public long VolumeNodeId { get; set; }

    /// <summary>The volume number the file's name gave when it was scanned (the stack it joins).</summary>
    public double Volume { get; set; }

    public long VolumeContentVersion { get; set; }

    /// <summary>The folder whose rows the answer belongs to (the series folder the Volumes view reads, unit subfolders merged).</summary>
    public long FolderId { get; set; }

    /// <summary><see cref="Features.VolumeScan.VolumeContentRowState"/>.</summary>
    public int State { get; set; }

    public string ChaptersJson { get; set; } = "[]";

    public int NearMisses { get; set; }
    public int CoveredPages { get; set; }

    /// <summary>A hash of the candidate set's (node id, content version) pairs and of the matcher's rules.</summary>
    public string CandidatesKey { get; set; } = string.Empty;

    /// <summary>An error code when the volume could not be scanned (State Failed / Skipped).</summary>
    public string? Error { get; set; }

    public DateTimeOffset ScannedAt { get; set; }
}

/// <summary>
/// The scan's singleton settings row (Id = 1): the admin switch "Find the chapters inside volume files" - on by default (owner 1.44.0
/// Q1: a new instance has no row, which reads as on; the migration writes none).
/// </summary>
public sealed class VolumeScanStateEntity
{
    public const long SingletonId = 1;

    public long Id { get; set; } = SingletonId;

    public bool Enabled { get; set; } = true;
}
