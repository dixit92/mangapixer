namespace com.lifepixer.mangapixer.Core.Reading;

/// <summary>
/// The appearance vocabulary (1.40.0 theming): the base themes and accents a user may store. The web keeps the same lists in
/// <c>web/src/app/core/theme/theme-vocabulary.ts</c>; <c>ThemeVocabularyPinTests</c> pins the two together, so a value added on
/// one side only fails the build instead of a 400 at runtime. A stored <c>null</c> means "the default".
/// </summary>
public static class ThemeVocabulary
{
    /// <summary>Base themes, in the order the web lists them. <c>system</c> follows the device (light or dark).</summary>
    public static IReadOnlyList<string> Bases { get; } = ["dark", "light", "black", "sepia", "system"];

    /// <summary>Accents, in the order the web lists them.</summary>
    public static IReadOnlyList<string> Accents { get; } = ["violet", "blue", "teal", "green", "amber", "rose"];

    /// <summary>The base a user gets until they choose one (owner decision, 1.40.0): dark, as before theming existed.</summary>
    public const string DefaultBase = "dark";

    /// <summary>The accent the app has always used.</summary>
    public const string DefaultAccent = "violet";

    public static bool IsBase(string? value) => value is not null && Bases.Contains(value, StringComparer.Ordinal);

    public static bool IsAccent(string? value) => value is not null && Accents.Contains(value, StringComparer.Ordinal);
}
