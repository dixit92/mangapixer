namespace com.lifepixer.mangapixer.Server.Tests.Features.WhatsNew;

/// <summary>
/// A synthetic CHANGELOG in the repository's Keep a Changelog shape (1.53.0 "What's new" tests): an <c>[Unreleased]</c> section on top
/// with text that must never show, four released versions out of order on purpose (2.1.0 sits below 2.0.1), wrapped bullets, the inline
/// subset and every kind of link. Never the real file's content.
/// </summary>
internal static class WhatsNewFixture
{
    public const string Changelog = """
        # Changelog

        All notable changes are documented in this file. See [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

        ## [Unreleased]

        ### Added

        - UNRELEASED-SECRET must never be shown.

        ## [2.2.0] - 2030-03-01

        ### Added

        - **Shared lanterns** are off until you turn them on in [Administration](/admin?tab=general). Each user can turn
          theirs off in Settings, and the `lantern:` search term
          finds them.
        - A plain item.

        ### Fixed

        - Links outside the app keep their text only: [the docs](docs/lanterns.md), [a site](https://example.org/x) and
          [a host](//example.org/y).

        ## [2.0.1] - 2030-01-15

        ### Fixed

        - The 2.0.1 fix.

        ## [2.1.0] - 2030-02-01

        ### Changed

        - The 2.1.0 change.

        ## [2.0.0] - 2030-01-01

        ### Added

        - The 2.0.0 feature.
        """;

    /// <summary>Writes <see cref="Changelog"/> (or <paramref name="text"/>) to a new file under <paramref name="folder"/>.</summary>
    public static string WriteTo(string folder, string? text = null)
    {
        var path = Path.Combine(folder, "whats-new-fixture-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(path, text ?? Changelog);
        return path;
    }
}
