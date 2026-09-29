namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>
/// The unit numbers one archive name states (1.29.0, <see cref="AutoMatchText.UnitsOf"/>): decimals kept
/// (<c>c045.5</c> -> 45.5), both numbers of a name that states both (<c>v03 c012</c>), a range as start / end
/// (<c>Vol. 01-05</c> -> 1 / 5; the end is null when the name states one number). <see cref="IsExtra"/>: the name's
/// own unit is fractional - the chapter when it states one (<c>c045.5</c>, a volume extra in manga), otherwise the
/// volume (<c>v02.5</c>). An extra is never missing and never fills a whole number. All null: no unit number.
/// </summary>
public readonly record struct UnitNumbers(decimal? Volume, decimal? VolumeEnd, decimal? Chapter, decimal? ChapterEnd, bool IsExtra)
{
    /// <summary>True when the name states no volume and no chapter number.</summary>
    public bool IsEmpty => Volume is null && Chapter is null;
}
