namespace com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// A folder's cover preference (1.32.0): what the covers of the works below it are built from. A folder without a row INHERITS
/// (the nearest ancestor with a row, then the library's "Show web covers" switch); there is no stored "inherit" value.
/// </summary>
public enum FolderCoverPreference
{
    /// <summary>Web covers when available (stored volume covers and posters), whatever the library switch says.</summary>
    Web = 0,

    /// <summary>The file's own cover: no web cover, poster or volume cover is shown or fetched for the works below.</summary>
    File = 1,
}

/// <summary>
/// The pure precedence rule of the per-folder cover preference (1.32.0). The nearest folder row (self first, then ancestors) is the
/// <c>folder</c> argument; null = no row anywhere above, so the library switch decides.
/// </summary>
public static class FolderCoverRules
{
    /// <summary>
    /// True when a web cover (stored volume cover, poster) may be SHOWN by the automatic layer: an explicit folder value wins over the
    /// library switch in both directions, so one subtree can differ either way.
    /// </summary>
    public static bool WebShown(FolderCoverPreference? folder, bool libraryHidden) => folder switch
    {
        FolderCoverPreference.Web => true,
        FolderCoverPreference.File => false,
        _ => !libraryHidden,
    };

    /// <summary>
    /// True when an ADMIN's explicit web cover choice may be shown: the inherited "File covers" never overrides an explicit choice, so it
    /// counts as no preference; only the library switch (unless the folder says Web covers) can still hide it.
    /// </summary>
    public static bool ChosenWebShown(FolderCoverPreference? folder, bool libraryHidden) =>
        folder == FolderCoverPreference.Web || !libraryHidden;

    /// <summary>True when the background work should not fetch or decide web covers for a work under this preference.</summary>
    public static bool SkipsWebWork(FolderCoverPreference? folder) => folder == FolderCoverPreference.File;
}
