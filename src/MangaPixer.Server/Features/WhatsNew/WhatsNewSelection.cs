namespace com.lifepixer.mangapixer.Server.Features.WhatsNew;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Updates;

/// <summary>
/// Which release-note sections an admin sees (1.53.0). Pure. Versions compare by their <c>major.minor.patch</c> core only - build
/// metadata (<c>+sha.abc</c>) and a pre-release label (<c>-rc.1</c>) are ignored, so <c>1.53.0-rc.1+sha.abc</c> runs as 1.53.0.
/// <list type="bullet">
/// <item>Not seen before (<c>LastSeenVersion</c> null or unreadable - every user from before 1.53.0): the running version's section
/// only; the version they came from is unknown.</item>
/// <item>Otherwise: every section newer than the last seen version and not newer than the running one, newest first.</item>
/// <item><c>currentOnly</c> (the menu's "What's new"): the running version's section only, seen or not.</item>
/// <item>A running version that cannot be read shows nothing.</item>
/// </list>
/// </summary>
public static class WhatsNewSelection
{
    /// <summary>The <c>major.minor.patch</c> core of a version string, or null when it cannot be read.</summary>
    public static (int Major, int Minor, int Patch)? Core(string? version)
    {
        var parsed = UpdateVersion.TryParse(version);
        return parsed is null ? null : (parsed.Value.Major, parsed.Value.Minor, parsed.Value.Patch);
    }

    /// <summary>The core as text (<c>1.53.0</c>), or null when the version cannot be read.</summary>
    public static string? CoreText(string? version) =>
        Core(version) is { } c ? $"{c.Major}.{c.Minor}.{c.Patch}" : null;

    public static IReadOnlyList<WhatsNewSectionDto> Select(
        IReadOnlyList<WhatsNewSectionDto> sections, string? running, string? lastSeen, bool currentOnly)
    {
        if (Core(running) is not { } run)
            return [];

        var seen = currentOnly ? null : Core(lastSeen);
        return sections
            .Select(s => (Section: s, Core: Core(s.Version)))
            .Where(x => x.Core is { } v && (seen is { } last
                ? v.CompareTo(last) > 0 && v.CompareTo(run) <= 0
                : v.CompareTo(run) == 0))
            .OrderByDescending(x => x.Core!.Value)
            .Select(x => x.Section)
            .ToList();
    }
}
