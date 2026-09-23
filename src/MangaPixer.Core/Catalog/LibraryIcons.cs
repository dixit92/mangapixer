namespace com.lifepixer.mangapixer.Core.Catalog;

/// <summary>
/// Curated allowlist of library icon names. Values are ligature names from the
/// classic Material Icons font (npm package <c>material-icons</c>) that the web
/// app ships via <c>node_modules/material-icons/iconfont/material-icons.css</c> -
/// every name here is verified to render in that font. Admins pick one of these
/// per library (or leave it unset for the name-derived default); the server
/// rejects anything else with 400 so a bad name can never end up stored and
/// silently fail to render.
/// </summary>
public static class LibraryIcons
{
    public static readonly IReadOnlyCollection<string> Allowed =
    [
        "menu_book",
        "auto_stories",
        "import_contacts",
        "local_library",
        "book",
        "bookmark",
        "bookmarks",
        "collections_bookmark",
        "library_books",
        "chrome_reader_mode",
        "star",
        "star_border",
        "favorite",
        "favorite_border",
        "bolt",
        "flash_on",
        "whatshot",
        "rocket_launch",
        "pets",
        "sports_martial_arts",
        "sports_kabaddi",
        "sports_esports",
        "theater_comedy",
        "brush",
        "palette",
        "color_lens",
        "category",
        "folder_special",
        "auto_awesome",
        "school",
        "explore",
        "public",
    ];

    private static readonly HashSet<string> AllowedSet = new(Allowed, StringComparer.Ordinal);

    public static bool IsValid(string? icon) => icon is null || AllowedSet.Contains(icon);
}
