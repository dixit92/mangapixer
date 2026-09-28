namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>
/// The automatic-lookups consent (owner decision 2: separate from the v1 "Fetch
/// from the web" consent, which keeps manual Identify working). The text lives in
/// the web client; a change of the automatic network surface bumps the version and
/// automatic work stops until an admin accepts the new text. v2 (1.28.0): the cover comparison downloads
/// (<see cref="AutoMatchCoverComparer"/>); a v1 consent is not carried over (owner, 2026-09-28).
/// </summary>
public static class MetadataAutoConsent
{
    public const int CurrentVersion = 2;
}

/// <summary>Threshold DTO mapping and validation (bounds from <see cref="MatchThresholds"/>).</summary>
public static class MetadataThresholds
{
    public static MetadataMatchThresholdsDto ToDto(MatchThresholds t) =>
        new() { AutoTitle = t.AutoTitle, Margin = t.Margin, ReviewFloor = t.ReviewFloor };

    public static MatchThresholds FromDto(MetadataMatchThresholdsDto dto) => new(dto.AutoTitle, dto.Margin, dto.ReviewFloor);

    public static MetadataMatchThresholdBoundsDto Bounds { get; } = new()
    {
        AutoTitleMin = MatchThresholds.AutoTitleMin,
        AutoTitleMax = MatchThresholds.AutoTitleMax,
        MarginMin = MatchThresholds.MarginMin,
        MarginMax = MatchThresholds.MarginMax,
        ReviewFloorMin = MatchThresholds.ReviewFloorMin,
        ReviewFloorMax = MatchThresholds.ReviewFloorMax,
    };

    /// <summary>The stored columns (null = default) resolved to effective thresholds; an invalid stored set falls back to the defaults.</summary>
    public static MatchThresholds Resolve(double? autoTitle, double? margin, double? reviewFloor)
    {
        var d = MatchThresholds.Default;
        var t = new MatchThresholds(autoTitle ?? d.AutoTitle, margin ?? d.Margin, reviewFloor ?? d.ReviewFloor);
        return t.IsValid ? t : d;
    }
}
