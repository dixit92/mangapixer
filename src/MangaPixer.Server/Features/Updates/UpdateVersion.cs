namespace com.lifepixer.mangapixer.Server.Features.Updates;

using System.Globalization;

/// <summary>
/// Pure SemVer-ish version parsing and comparison for the Update Checker. No I/O,
/// no clock — every branch is unit-testable in isolation.
///
/// GitHub release tags arrive as <c>tag_name</c> strings that may carry a leading
/// <c>v</c> (e.g. <c>"v1.21.0"</c>), an optional pre-release suffix
/// (<c>"1.21.0-rc.1"</c>) and optional build metadata (<c>"1.21.0+sha.abc"</c>).
/// The server's own version is the assembly <c>InformationalVersion</c>, which
/// likewise may include <c>+build</c> metadata. Both are normalized the same way
/// before comparison.
///
/// Comparison follows the parts of SemVer 2.0.0 that matter here:
/// build metadata is ignored, numeric core components compare numerically, and a
/// pre-release version sorts <em>below</em> the same core release
/// (<c>1.21.0-rc.1 &lt; 1.21.0</c>).
/// </summary>
public static class UpdateVersion
{
    /// <summary>
    /// A parsed version: three numeric core parts plus an optional pre-release
    /// label. Build metadata is discarded during parsing.
    /// </summary>
    public readonly record struct Parsed(int Major, int Minor, int Patch, string? PreRelease);

    /// <summary>
    /// Strips a single leading <c>v</c>/<c>V</c> and any <c>+build</c> metadata,
    /// then parses <c>major.minor.patch</c> with an optional <c>-prerelease</c>.
    /// Missing minor/patch default to 0 (so <c>"2"</c> parses as 2.0.0). Returns
    /// null when the core cannot be read as numbers, so callers degrade instead
    /// of throwing on a malformed upstream tag.
    /// </summary>
    public static Parsed? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
            s = s[1..];

        // Drop build metadata (everything from the first '+').
        var plus = s.IndexOf('+');
        if (plus >= 0)
            s = s[..plus];

        // Split off the pre-release label (everything after the first '-').
        string? pre = null;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
        }

        if (s.Length == 0)
            return null;

        var parts = s.Split('.');
        if (parts.Length > 3)
            return null;

        var core = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (i >= parts.Length)
            {
                core[i] = 0;
                continue;
            }

            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return null;
            core[i] = value;
        }

        return new Parsed(core[0], core[1], core[2], string.IsNullOrEmpty(pre) ? null : pre);
    }

    /// <summary>
    /// Orders two parsed versions: negative if <paramref name="a"/> precedes
    /// <paramref name="b"/>, zero if equal (ignoring build metadata), positive if
    /// after. Pre-release versions sort below the matching core release.
    /// </summary>
    public static int Compare(Parsed a, Parsed b)
    {
        var core = a.Major.CompareTo(b.Major);
        if (core != 0) return core;
        core = a.Minor.CompareTo(b.Minor);
        if (core != 0) return core;
        core = a.Patch.CompareTo(b.Patch);
        if (core != 0) return core;

        // Equal core: a version WITH a pre-release is lower than one without.
        if (a.PreRelease is null && b.PreRelease is null) return 0;
        if (a.PreRelease is null) return 1;   // a is the release, b is pre-release
        if (b.PreRelease is null) return -1;  // a is pre-release, b is the release

        // Both pre-release: dotted identifier comparison (numeric identifiers
        // compare numerically, and fewer identifiers rank lower when otherwise
        // equal — SemVer 2.0.0 §11).
        return ComparePreRelease(a.PreRelease, b.PreRelease);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a strictly newer version than
    /// <paramref name="current"/>. Unparseable input on either side yields false
    /// (we never claim an update from a version we cannot understand).
    /// </summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        var c = TryParse(candidate);
        var cur = TryParse(current);
        if (c is null || cur is null)
            return false;
        return Compare(c.Value, cur.Value) > 0;
    }

    private static int ComparePreRelease(string a, string b)
    {
        var ai = a.Split('.');
        var bi = b.Split('.');
        var len = Math.Min(ai.Length, bi.Length);
        for (var i = 0; i < len; i++)
        {
            var aNum = int.TryParse(ai[i], NumberStyles.None, CultureInfo.InvariantCulture, out var an);
            var bNum = int.TryParse(bi[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bn);

            int cmp;
            if (aNum && bNum)
                cmp = an.CompareTo(bn);            // both numeric → numeric compare
            else if (aNum)
                cmp = -1;                          // numeric identifiers rank below alphanumeric
            else if (bNum)
                cmp = 1;
            else
                cmp = string.CompareOrdinal(ai[i], bi[i]);

            if (cmp != 0) return cmp;
        }

        return ai.Length.CompareTo(bi.Length);
    }
}
