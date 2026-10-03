namespace com.lifepixer.mangapixer.Server.Features.Export;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// The export's wire vocabulary (1.33.0), mapped explicitly so a renamed C# member never changes the contract. The sample fixture
/// (<c>contracts/samples/metadata-export-v1.json</c>) pins every value.
/// </summary>
public static class ExportVocabulary
{
    public const string Folder = "folder";
    public const string Archive = "archive";

    public const string NodeGone = "nodeGone";
    public const string LinkCleared = "linkCleared";
    public const string MovedToOtherLibrary = "movedToOtherLibrary";

    public const string MangaDex = "mangadex";
    public const string Wikipedia = "wikipedia";
    public const string Merged = "merged";

    public const string Released = "released";
    public const string Announced = "announced";

    public static string LinkState(SeriesLinkState state) => state switch
    {
        SeriesLinkState.Confirmed => "Confirmed",
        SeriesLinkState.Auto => "Auto",
        SeriesLinkState.NeedsReview => "NeedsReview",
        SeriesLinkState.DontMatch => "DontMatch",
        _ => "Unknown",
    };

    public static string? Method(int? method) => (MetadataMatchMethod?)method switch
    {
        null => null,
        MetadataMatchMethod.Search => "search",
        MetadataMatchMethod.Reference => "reference",
        MetadataMatchMethod.ComicInfoWebHint => "comicInfo",
        MetadataMatchMethod.Auto => "auto",
        _ => null,
    };

    public static string? Status(MetadataOriginStatus? status) => status switch
    {
        MetadataOriginStatus.Ongoing => "Ongoing",
        MetadataOriginStatus.Complete => "Complete",
        MetadataOriginStatus.Hiatus => "Hiatus",
        MetadataOriginStatus.Cancelled => "Cancelled",
        _ => null,
    };

    public static string Answer(SeriesAnswer answer) => answer switch
    {
        SeriesAnswer.HaveItAll => "HaveItAll",
        SeriesAnswer.FinishedMissing => "FinishedMissing",
        SeriesAnswer.UpToDate => "UpToDate",
        SeriesAnswer.MissingSome => "MissingSome",
        _ => "CantTell",
    };

    public static string Reason(SeriesAnswerReason reason) => reason switch
    {
        SeriesAnswerReason.Running => "Running",
        SeriesAnswerReason.OnHiatus => "OnHiatus",
        SeriesAnswerReason.StatusUnknown => "StatusUnknown",
        SeriesAnswerReason.WaitingForLanguage => "WaitingForLanguage",
        SeriesAnswerReason.LanguageEditionDropped => "LanguageEditionDropped",
        SeriesAnswerReason.NoNumbers => "NoNumbers",
        SeriesAnswerReason.NumberingRestarts => "NumberingRestarts",
        SeriesAnswerReason.NothingKnownReleased => "NothingKnownReleased",
        SeriesAnswerReason.NoVolumeTotal => "NoVolumeTotal",
        SeriesAnswerReason.OneShot => "OneShot",
        _ => "None",
    };

    /// <summary>An official MangaDex link (its <c>links</c> key) as an export link; null for a key the export does not show.</summary>
    public static ExportOfficialLinkDto? OfficialLink(string key, string url) => key switch
    {
        "raw" => Link("publisher", "Official (original language)", url),
        "engtl" => Link("publisher", "Official English release", url),
        "bw" => Link("store", "BookWalker", url),
        "amz" => Link("store", "Amazon", url),
        "ebj" => Link("store", "ebookjapan", url),
        "cdj" => Link("store", "CDJapan", url),
        _ => null,
    };

    private static ExportOfficialLinkDto Link(string kind, string label, string url) =>
        new() { Kind = kind, Label = label, Url = url, Source = MangaDex };
}
