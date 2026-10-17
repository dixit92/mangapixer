namespace com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// Admin > Performance, "Find the chapters inside volume files" (1.44.0 volume contents scan): the switch, whether configuration
/// switched it off (<c>MangaPixer:VolumeScan:Enabled=false</c>), and counts only.
/// </summary>
public sealed record VolumeScanStatusDto
{
    /// <summary>The admin switch (on by default).</summary>
    public required bool Enabled { get; init; }

    /// <summary>True when configuration switches the scan off whatever the admin switch says.</summary>
    public required bool ConfigDisabled { get; init; }

    /// <summary>Volume files checked (an answer is stored for them).</summary>
    public required int VolumesChecked { get; init; }

    /// <summary>Volume files the scan found chapter files inside.</summary>
    public required int VolumesWithChapters { get; init; }

    /// <summary>Chapter files found inside a volume file.</summary>
    public required int ChaptersInside { get; init; }

    /// <summary>Folders the current or last sweep still had to check (null before the first sweep of this process).</summary>
    public int? FoldersWaiting { get; init; }

    /// <summary>True while a sweep runs.</summary>
    public required bool Running { get; init; }
}

/// <summary>PUT body of <c>/api/v1/admin/volume-contents</c>.</summary>
public sealed record UpdateVolumeScanRequest
{
    public required bool Enabled { get; init; }
}

/// <summary>
/// 1.44.0: the chapter files the volume contents scan found inside a stack's volume file (<c>Chapters 51-58 are inside the volume
/// file</c>): the first and last chapter number and how many files. Left out when there are none.
/// </summary>
public sealed record InVolumeFileDto
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required int Count { get; init; }
}
