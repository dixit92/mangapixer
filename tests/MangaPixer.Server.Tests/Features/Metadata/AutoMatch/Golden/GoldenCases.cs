namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch.Golden;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>
/// One golden case: a synthetic folder (PUBLIC, well-known titles rendered as folder / archive
/// names - never anything from a real library) and the expected detector class, band and chosen
/// MangaUpdates id. <see cref="GroupTitle"/> selects an archive group (archive-level cases);
/// <see cref="Band"/> null makes it a detector-only case (no provider data). <see cref="Vetoes"/>, when
/// set, is the exact set of auto-vetoing reasons the top candidate must carry.
/// <see cref="LocalCover"/> (1.28.0) is the series id whose stored local cover hash stands for the work's thumbnail: the
/// case then runs the cover comparison, and <see cref="CoverImages"/> is the exact number of candidate images it must
/// download, <see cref="CoverMatchOnTop"/> whether the top candidate carries the cover evidence. <see cref="Declared"/>
/// (1.28.0) is what an admin declared for the folder (lane D's facts), applied as the lookup applies it - scoring evidence only
/// (1.30.0: the declared-type search filter is retired; a search that sends any type beyond the fixed filter finds no recording).
/// </summary>
public sealed record GoldenCase(
    string Id,
    FolderShape Folder,
    WorkClass? Class = null,
    MatchBand? Band = null,
    string? ExpectedId = null,
    string? ComicInfo = null,
    bool DoujinAllowed = false,
    string? GroupTitle = null,
    ContentSuggestion? Content = null,
    MatchReason? Vetoes = null,
    string? LocalCover = null,
    int? CoverImages = null,
    bool? CoverMatchOnTop = null,
    DeclaredFacts? Declared = null)
{
    public override string ToString() => Id;
}

/// <summary>The golden set (design section 2 "How to measure and tune").</summary>
public static class GoldenCases
{
    private static FolderShape F(string name, IEnumerable<string> archives, string? category = null, string? parent = null,
        (string Name, int Count)[]? subs = null, int depth = 2, string[]? authors = null) =>
        new(name, depth, archives.ToList(), (subs ?? []).Select(s => new ChildFolderShape(s.Name, s.Count)).ToList(), parent, category, authors);

    /// <summary>
    /// The author names the server hands to the detector when records by them are linked in the library (1.28.0:
    /// <c>LibraryTreeSnapshot.ProviderAuthorSet</c>, the provider-author half of the artist-folder rule). Spelled as
    /// MangaUpdates lists them.
    /// </summary>
    private static readonly string[] s_linkedAuthors = ["FUJIMOTO Tatsuki", "URASAWA Naoki", "OTOMO Katsuhiro"];

    private static IEnumerable<string> Vols(string title, int n, string suffix = "") =>
        Enumerable.Range(1, n).Select(i => string.Create(CultureInfo.InvariantCulture, $"{title} v{i:00}{suffix}.cbz"));

    private static IEnumerable<string> Chaps(string title, int n) =>
        Enumerable.Range(1, n).Select(i => string.Create(CultureInfo.InvariantCulture, $"{title} - Chapter {i:000}.cbz"));

    private static IEnumerable<string> ChapTokens(string title, int n) =>
        Enumerable.Range(1, n).Select(i => string.Create(CultureInfo.InvariantCulture, $"{title} Ch. {i:000}.cbz"));

    private static IEnumerable<string> Units(int n) =>
        Enumerable.Range(1, n).Select(i => string.Create(CultureInfo.InvariantCulture, $"{i:000} [Chapter Title {i}].cbz"));

    private const string Berserk = "51239621230";
    private const string DungeonMeshi = "19088665446";
    private const string AttackOnTitan = "23393951235";
    private const string SoloLeveling = "15180124327";
    private const string VinlandSaga = "27728982867";
    private const string OnePiece = "55099564912";
    private const string ChainsawMan = "75336092483";
    private const string LookBack = "62512335978";
    private const string SayonaraEri = "47603342373";
    private const string JoJoPart3 = "60420553585";
    private const string JoJoPart4 = "42553317740";
    private const string TokyoGhoul = "26272522291";
    private const string MobPsycho = "605012986";
    private const string TwentiethCenturyBoys = "45334600346";
    private const string TwentyFirstCenturyBoys = "64832756793";
    private const string Yotsuba = "23606352927";
    private const string HunterXHunter = "49449837876";
    private const string JigokurakuKaku = "61508275290";
    private const string Frieren = "66296374554";
    private const string SpyXFamily = "67814124606";
    private const string TowerOfGod = "13015731700";
    private const string OmniscientReader = "50369844984";
    private const string MonsterUrasawa = "72274276213";
    private const string Akira = "46397795369";
    private const string Punpun = "21944750964";
    private const string FirePunch = "32334361267";
    private const string YotsubaDjYanda = "57918701059";
    private const string TenseiKizoku = "46692009496";
    private const string IsekaiCheatSkill = "15495823031";
    private const string Kingdom = "4324727424";
    private const string BerserkOfGluttonyComic = "74072114866";
    private const string Jigokuraku2005 = "10294535868";
    private const string WindBreakerKorea = "TODO-KR";
    private const string WindBreakerJapan = "TODO-JP";

    private static readonly string[] s_artistFolder =
    [
        "[Fujimoto Tatsuki] Look Back (2021) (Digital).cbz",
        "[Fujimoto Tatsuki] Sayonara Eri (2022) (Digital).cbz",
        "[Fujimoto Tatsuki] Fire Punch v01.cbz",
        "[Fujimoto Tatsuki] Fire Punch v02.cbz",
        "[Fujimoto Tatsuki] Fire Punch v03.cbz",
        "[Fujimoto Tatsuki] Berserk v01.cbz",
    ];

    // The same works without the creator tag: by shape alone neither one work nor a collection (review only).
    private static readonly string[] s_untaggedArtistFolder =
    [
        "Look Back (2021) (Digital).cbz",
        "Sayonara Eri (2022) (Digital).cbz",
        "Fire Punch v01.cbz",
        "Fire Punch v02.cbz",
        "Fire Punch v03.cbz",
        "Berserk v01.cbz",
    ];

    private static readonly string[] s_collectionLeaf =
    [
        "Look Back.cbz",
        "Sayonara Eri.cbz",
        "Hunter x Hunter v01.cbz",
        "Akira v01.cbz",
        "Oyasumi Punpun v01.cbz",
    ];

    // Doujin naming anatomy; the artist is the provider-listed author of the recorded dj record.
    private static readonly string[] s_doujinShelf =
    [
        "(Comic Event 72) [Circle Placeholder (Hideyoshico)] Yanda&! 1 (Yotsuba).cbz",
        "(Comic Event 73) [Circle Placeholder (Hideyoshico)] Yanda&! 2 (Yotsuba).cbz",
        "(Comic Event 74) [Circle Placeholder (Hideyoshico)] Yanda&! 3 (Yotsuba).cbz",
        "(Comic Event 80) [Other Circle (Other Artist)] Another Story (Some Parody).cbz",
        "[Third Circle (Third Artist)] A Third Story (Other Parody).cbz",
        "[Fourth Circle (Fourth Artist)] A Fourth Story (Other Parody).cbz",
        "(Comic Event 81) [Fifth Circle (Fifth Artist)] A Fifth Story (Some Parody).cbz",
    ];

    private static readonly string[] s_doujinShelfLone =
    [
        "(Comic Event 72) [Circle Placeholder (Hideyoshico)] Yanda&! (Yotsuba).cbz",
        "(Comic Event 80) [Other Circle (Other Artist)] Another Story (Some Parody).cbz",
        "[Third Circle (Third Artist)] A Third Story (Other Parody).cbz",
    ];

    public static IReadOnlyList<GoldenCase> All { get; } =
    [
        // --- Folder level: romaji / English / scene-style names -------------------------------
        new("F01 romaji, digital volumes", F("Berserk", Vols("Berserk", 41, " (Digital)"), "Manga", "Manga"), WorkClass.Series, MatchBand.Auto, Berserk),
        new("F02 romaji + [English], English archive names", F("Dungeon Meshi [Delicious in Dungeon]", Vols("Delicious in Dungeon", 14, " (2017) (Digital)")), WorkClass.Series, MatchBand.Auto, DungeonMeshi),
        new("F03 English folder name finds the romaji record", F("Delicious in Dungeon", Vols("Delicious in Dungeon", 14)), WorkClass.Series, MatchBand.Auto, DungeonMeshi),
        new("F04 chapter archives", F("Dungeon Meshi", Chaps("Dungeon Meshi", 97)), WorkClass.Series, MatchBand.Auto, DungeonMeshi),
        new("F05 romaji, chapters (novel twins filtered)", F("Shingeki no Kyojin", Chaps("Shingeki no Kyojin", 139)), WorkClass.Series, MatchBand.Auto, AttackOnTitan),
        new("F06 English name, volumes", F("Attack on Titan", Vols("Attack on Titan", 34)), WorkClass.Series, MatchBand.Auto, AttackOnTitan),
        new("F07 manhwa under a Manhwa category", F("Solo Leveling", Units(200), "Manhwa"), WorkClass.Series, MatchBand.Auto, SoloLeveling),
        // 1.27.0 band change (intended): the category hint is positive-only (owner option a'), so a manhwa filed
        // under a "Manga" folder auto-links instead of going to review with a type conflict.
        new("F08 manhwa under a Manga category: the hint is positive-only, still auto", F("Solo Leveling", Units(200), "Manga"), WorkClass.Series, MatchBand.Auto, SoloLeveling, Vetoes: MatchReason.None),
        new("F09 scene-style archive names", F("Vinland Saga", Vols("Vinland Saga", 12, " (2013) (Digital) (Scan Team)")), WorkClass.Series, MatchBand.Auto, VinlandSaga),
        new("F10 meaningless folder name, ComicInfo series", F("Unsorted Batch", Vols("Vinland Saga", 5)), WorkClass.Series, MatchBand.Auto, VinlandSaga, ComicInfo: "Vinland Saga"),
        new("F11 long-running chapters", F("One Piece", Chaps("One Piece", 1100)), WorkClass.Series, MatchBand.Auto, OnePiece),
        new("F12 volumes", F("Chainsaw Man", Vols("Chainsaw Man", 20)), WorkClass.Series, MatchBand.Auto, ChainsawMan),
        new("F13 one-shot folder, disambiguated provider title", F("Look Back", ["Look Back (2021) (Digital).cbz"]), WorkClass.OneShot, MatchBand.Auto, LookBack),
        new("F14 one-shot title with ten volume archives: count conflict", F("Look Back", Vols("Look Back", 10)), WorkClass.Series, MatchBand.NeedsReview, LookBack, Vetoes: MatchReason.CountConflict),
        new("F15 one-shot folder, romaji", F("Sayonara Eri", ["Sayonara Eri.cbz"]), WorkClass.OneShot, MatchBand.Auto, SayonaraEri),
        new("F16 one-shot folder, English", F("Goodbye, Eri", ["Goodbye, Eri.cbz"]), WorkClass.OneShot, MatchBand.Auto, SayonaraEri),
        new("F17 numbered part with subtitle", F("JoJo no Kimyou na Bouken Part 3 - Stardust Crusaders", Vols("JoJo no Kimyou na Bouken Part 3", 16), depth: 3), WorkClass.Series, MatchBand.Auto, JoJoPart3),
        new("F18 numbered part, the provider ranks another part first", F("JoJo no Kimyou na Bouken Part 4 - Diamond wa Kudakenai", Vols("JoJo no Kimyou na Bouken Part 4", 18), depth: 3), WorkClass.Series, MatchBand.Auto, JoJoPart4),
        new("F19 short romaji; official doujin + subtitled main record", F("Kaguya-sama wa Kokurasetai", Vols("Kaguya-sama wa Kokurasetai", 28)), WorkClass.Series, MatchBand.NeedsReview),
        new("F20 main record absent from the search results", F("Re Zero kara Hajimeru Isekai Seikatsu", Chaps("Re Zero kara Hajimeru Isekai Seikatsu", 50)), WorkClass.Series, MatchBand.NeedsReview),
        new("F21 sequel exists (:re)", F("Tokyo Ghoul", Vols("Tokyo Ghoul", 14)), WorkClass.Series, MatchBand.Auto, TokyoGhoul),
        new("F22 sequel folder whose record the search misses (the prequel must not count as right)", F("Tokyo Ghoul re", Vols("Tokyo Ghoul re", 16)), WorkClass.Series, MatchBand.NeedsReview),
        new("F23 number that is part of the name", F("Mob Psycho 100", Vols("Mob Psycho 100", 16)), WorkClass.Series, MatchBand.Auto, MobPsycho),
        new("F24 leading number, English alt title", F("20th Century Boys", Vols("20th Century Boys", 22)), WorkClass.Series, MatchBand.Auto, TwentiethCenturyBoys),
        new("F25 sequel of F24", F("21st Century Boys", Vols("21st Century Boys", 2)), WorkClass.Series, MatchBand.Auto, TwentyFirstCenturyBoys),
        new("F26 punctuation title", F("Yotsuba to!", Vols("Yotsuba to!", 15)), WorkClass.Series, MatchBand.Auto, Yotsuba),
        new("F27 x-titled, look-alike supersets", F("Hunter x Hunter", Vols("Hunter x Hunter", 37)), WorkClass.Series, MatchBand.Auto, HunterXHunter),
        new("F28 three same-titled records (disambiguated)", F("Jigokuraku [Hell's Paradise]", Vols("Hell's Paradise - Jigokuraku", 13, " (2019)")), WorkClass.Series, MatchBand.NeedsReview, JigokurakuKaku),
        new("F29 long vowel romaji", F("Sousou no Frieren", Vols("Sousou no Frieren", 13)), WorkClass.Series, MatchBand.Auto, Frieren),
        new("F30 English with subtitle and apostrophe", F("Frieren - Beyond Journey's End", Vols("Frieren - Beyond Journey's End", 13)), WorkClass.Series, MatchBand.Auto, Frieren),
        new("F31 x-titled", F("Spy x Family", Vols("Spy x Family", 13)), WorkClass.Series, MatchBand.Auto, SpyXFamily),
        new("F32 webtoon chapters under Manhwa", F("Tower of God", Units(600), "Manhwa"), WorkClass.Series, MatchBand.Auto, TowerOfGod),
        new("F33 English webtoon name with apostrophe", F("Omniscient Reader's Viewpoint", Units(200), "Manhwa"), WorkClass.Series, MatchBand.Auto, OmniscientReader),
        new("F34 generic one-word title under an author folder", F("Monster", Vols("Monster", 18), parent: "Urasawa Naoki"), WorkClass.Series, MatchBand.Auto, MonsterUrasawa),
        new("F35 one-word title", F("Akira", Vols("Akira", 6)), WorkClass.Series, MatchBand.Auto, Akira),
        new("F36 romaji", F("Oyasumi Punpun", Vols("Oyasumi Punpun", 13)), WorkClass.Series, MatchBand.Auto, Punpun),
        new("F37 volumes", F("Fire Punch", Vols("Fire Punch", 8)), WorkClass.Series, MatchBand.Auto, FirePunch),
        new("F38 nothing on the provider", F("Zzqx Nonexistent Synthetic Title", Vols("Zzqx Nonexistent Synthetic Title", 3)), WorkClass.Series, MatchBand.Unmatched),
        new("F39 files older than the series: year conflict", F("Berserk", Vols("Berserk", 5, " (1985)")), WorkClass.Series, MatchBand.NeedsReview, Berserk, Vetoes: MatchReason.YearConflict),
        new("F40 unit subfolders only", F("Chainsaw Man", [], subs: [("Volumes", 11), ("Chapters", 80)]), WorkClass.SeriesWithUnits, MatchBand.Auto, ChainsawMan),
        new("F41 leading scan-group tag on the folder", F("[Scan Team] Hunter x Hunter", Vols("Hunter x Hunter", 37)), WorkClass.Series, MatchBand.Auto, HunterXHunter),
        new("F42 underscores", F("Sousou_no_Frieren", Vols("Sousou_no_Frieren", 13)), WorkClass.Series, MatchBand.Auto, Frieren),
        new("F43 doujinshi allowed: dj look-alikes stay below the series", F("Yotsuba to!", Vols("Yotsuba to!", 15)), WorkClass.Series, MatchBand.Auto, Yotsuba, DoujinAllowed: true),
        new("F44 category agrees", F("Shingeki no Kyojin", Units(139), "Manga"), WorkClass.Series, MatchBand.Auto, AttackOnTitan),
        new("F45 season subfolders", F("Tower of God", [], "Manhwa", subs: [("Season 1", 80), ("Season 2", 330), ("Season 3", 190)]), WorkClass.SeriesWithUnits, MatchBand.Auto, TowerOfGod),

        // --- Archive level: artist folder, collection leaf, doujin -------------------------
        new("A01 artist folder: one-shot", F("Fujimoto Tatsuki", s_artistFolder), WorkClass.ArtistCollection, MatchBand.Auto, LookBack, GroupTitle: "Look Back"),
        new("A02 artist folder: second one-shot", F("Fujimoto Tatsuki", s_artistFolder), WorkClass.ArtistCollection, MatchBand.Auto, SayonaraEri, GroupTitle: "Sayonara Eri"),
        new("A03 artist folder: numbered volumes grouped", F("Fujimoto Tatsuki", s_artistFolder), WorkClass.ArtistCollection, MatchBand.Auto, FirePunch, GroupTitle: "Fire Punch"),
        new("A04 artist folder: a mis-tagged volume, the author conflict alone vetoes auto", F("Fujimoto Tatsuki", s_artistFolder), WorkClass.ArtistCollection, MatchBand.NeedsReview, Berserk,
            GroupTitle: "Berserk", Vetoes: MatchReason.AuthorConflict),
        new("A05 collection leaf: a lone volume of a long series", F("Shelf", s_collectionLeaf), WorkClass.CollectionLeaf, MatchBand.Auto, HunterXHunter, GroupTitle: "Hunter x Hunter"),
        new("A06 collection leaf: one-shot", F("Shelf", s_collectionLeaf), WorkClass.CollectionLeaf, MatchBand.Auto, LookBack, GroupTitle: "Look Back"),
        new("A07 doujin anatomy: numbered dj mini-series, parody dj form, author agrees", F("Doujin Shelf", s_doujinShelf), WorkClass.CollectionLeaf, MatchBand.Auto,
            YotsubaDjYanda, DoujinAllowed: true, GroupTitle: "Yanda&", Content: ContentSuggestion.DoujinshiAndAdultOneShots),
        new("A08 ambiguous folder: review only", F("Vinland Saga",
            ["Vinland Saga 1.cbz", "Vinland Saga 2.cbz", "Vinland Saga 3.cbz", "Alpha Story.cbz", "Beta Tale.cbz", "Gamma Saga.cbz"]),
            WorkClass.Ambiguous, MatchBand.NeedsReview, VinlandSaga),
        new("A10 doujin anatomy: a lone archive of a 7-volume dj record links to it (one archive may hold the whole series)", F("Doujin Shelf", s_doujinShelfLone), WorkClass.CollectionLeaf,
            MatchBand.Auto, YotsubaDjYanda, DoujinAllowed: true, GroupTitle: "Yanda&", Content: ContentSuggestion.DoujinshiAndAdultOneShots),
        // A Mixed folder is planned the way production plans it (owner decision on loose archives, 2026-09-26): a loose
        // archive that is its own work is matched on its own (archive level); loose UNITS of one work keep the folder
        // review-only. (Before 1.27.0 A09 planned the whole folder, a path production no longer takes.)
        new("A09 mixed folder: a loose archive that is its own work is matched on its own",
            F("Berserk", ["Berserk v01.cbz"], subs: [("Berserk Gaiden", 2)]), WorkClass.Mixed, MatchBand.Auto, Berserk, GroupTitle: "Berserk"),
        new("A09b mixed folder: loose units of one work keep the folder review-only",
            F("Berserk", ["Berserk v01.cbz", "Berserk v02.cbz", "Berserk v03.cbz"], subs: [("Berserk Gaiden", 2)]), WorkClass.Mixed, MatchBand.NeedsReview, Berserk),

        // --- 1.28.0: the provider-author half of the artist-folder rule -----------------------------
        // An untagged folder named like the author of a record linked in the library: an artist collection, matched
        // archive by archive with the author required to agree (without the linked author: review only, P00 below).
        new("P01 provider-author artist folder, untagged names: one-shot", F("Fujimoto Tatsuki", s_untaggedArtistFolder, authors: s_linkedAuthors),
            WorkClass.ArtistCollection, MatchBand.Auto, LookBack, GroupTitle: "Look Back"),
        new("P02 provider-author artist folder, untagged names: numbered volumes grouped", F("Fujimoto Tatsuki", s_untaggedArtistFolder, authors: s_linkedAuthors),
            WorkClass.ArtistCollection, MatchBand.Auto, FirePunch, GroupTitle: "Fire Punch"),
        new("P03 provider-author artist folder: a mis-filed volume, the author conflict alone vetoes auto", F("Fujimoto Tatsuki", s_untaggedArtistFolder, authors: s_linkedAuthors),
            WorkClass.ArtistCollection, MatchBand.NeedsReview, Berserk, GroupTitle: "Berserk", Vetoes: MatchReason.AuthorConflict),
        // The other direction: a series whose title is also a linked author's name (a pen name) keeps its series shape.
        new("P04 a series named like a linked author stays a series", F("Akira", Vols("Akira", 6), authors: ["Akira", .. s_linkedAuthors]),
            WorkClass.Series, MatchBand.Auto, Akira),
        new("P05 a one-archive folder named like a linked author stays a one-shot", F("Fujimoto Tatsuki", ["Look Back (2021) (Digital).cbz"], authors: s_linkedAuthors),
            WorkClass.OneShot),
        new("P00 the untagged artist folder without a linked author: review only (the 1.27.0 class)", F("Fujimoto Tatsuki", s_untaggedArtistFolder),
            WorkClass.Ambiguous),

        // --- 1.28.0: cover similarity as tie-break evidence ----------------------------------------
        // Local covers are the provider cover of the right record, cropped 3% per side (a scan's framing), as the stored
        // thumbnail. Positive only and on the adjusted score: at the default 10-point lead a tie stays in review - the
        // cover puts the right record first; it never turns a weak title into an automatic link.
        new("C01 cover: three same-titled records, the local volume 1 is the KAKU cover", F("Jigokuraku [Hell's Paradise]",
            Vols("Hell's Paradise - Jigokuraku", 13, " (2019)")), WorkClass.Series, MatchBand.NeedsReview, JigokurakuKaku,
            LocalCover: "61508275290", CoverImages: 2, CoverMatchOnTop: true),
        // Three records titled "Jigokuraku" tie at 1.00; the folder has no disambiguator, so without the cover the 2005
        // record ranks first (the adjusted scores tie too). The cover puts the KAKU record first.
        new("C02 cover: an undisambiguated folder of the KAKU volumes - the cover puts that record first", F("Jigokuraku",
            Vols("Jigokuraku", 2)), WorkClass.Series, MatchBand.NeedsReview, JigokurakuKaku,
            LocalCover: "61508275290", CoverImages: 2, CoverMatchOnTop: true),
        // The limit, measured: only the top two are compared (at most two images per work); a third tied record is not.
        new("C05 cover: the right record is third in a three-way tie - no signal, the order stays", F("Jigokuraku",
            Vols("Jigokuraku", 2)), WorkClass.Series, MatchBand.NeedsReview, Jigokuraku2005,
            LocalCover: "76554797640", CoverImages: 2, CoverMatchOnTop: false),
        new("C03 cover: series vs its anthology on a tied head, the series cover", F("Tensei Kizoku no Isekai Boukenroku",
            Vols("Tensei Kizoku no Isekai Boukenroku", 5)), WorkClass.Series, MatchBand.NeedsReview, TenseiKizoku,
            LocalCover: "46692009496", CoverImages: 2, CoverMatchOnTop: true),
        new("C04 cover: a chapter folder is never compared (its first page is not a cover)", F("Re Zero kara Hajimeru Isekai Seikatsu",
            Chaps("Re Zero kara Hajimeru Isekai Seikatsu", 50)), WorkClass.Series, MatchBand.NeedsReview,
            LocalCover: "46692009496", CoverImages: 0, CoverMatchOnTop: false),

        // --- 1.28.0: declared facts as evidence; 1.30.0: the declared type a strong hint, never a search filter --------------
        // Declared creators are creator hints (positive only). The declared type raises a record of its implied origin and
        // lowers one that contradicts it (+0.05 / -0.05, never a veto); nothing declared is sent.
        new("H01 declared manhwa (no category folder): the Korean record agrees", F("Solo Leveling", Units(200)),
            WorkClass.Series, MatchBand.Auto, SoloLeveling, Vetoes: MatchReason.None, Declared: new(DeclaredFactKeys.TypeSlug(DeclaredType.Manhwa), [])),
        new("H02 a wrong declared type (manga for a manhwa) is no veto: still auto", F("Solo Leveling", Units(200)),
            WorkClass.Series, MatchBand.Auto, SoloLeveling, Vetoes: MatchReason.None, Declared: new(DeclaredFactKeys.TypeSlug(DeclaredType.Manga), [])),
        // 1.30.0 band change (intended, owner): with the search filter (1.28.0) a WRONG declared type kept the right record out of
        // the search - unmatched. Now the search is the fixed filter and the mismatch only lowers the record: still auto.
        new("H06 a wrong declared type (manhwa for a manga) no longer hides the right record", F("Chainsaw Man", Vols("Chainsaw Man", 20)),
            WorkClass.Series, MatchBand.Auto, ChainsawMan, Vetoes: MatchReason.None, Declared: new(DeclaredFactKeys.TypeSlug(DeclaredType.Manhwa), [])),
        // Three records titled "Jigokuraku" tie at 1.00 (C02 / C05); the declared author names one of them.
        new("H03 declared creator: an undisambiguated one-word title, the declared author picks the record", F("Jigokuraku", Vols("Jigokuraku", 2)),
            WorkClass.Series, MatchBand.Auto, JigokurakuKaku, Declared: new(null, [new DeclaredCreator("Kaku Yuuji", "author")])),
        new("H04 a declared creator no candidate has changes nothing (the order stays)", F("Jigokuraku", Vols("Jigokuraku", 2)),
            WorkClass.Series, MatchBand.NeedsReview, Jigokuraku2005, Declared: new(null, [new DeclaredCreator("Nobody Synthetic", null)])),
        // A Japanese manga and a Korean webtoon share the exact title "Wind Breaker" (1.30.0): undeclared, a tie for review; the
        // declared type settles it either way.
        new("H07 an origin tie on the title stays in review without a declaration", F("Wind Breaker", Chaps("Wind Breaker", 40)),
            WorkClass.Series, MatchBand.NeedsReview),
        new("H08 declared manhwa: the Korean \"Wind Breaker\"", F("Wind Breaker", Chaps("Wind Breaker", 40)),
            WorkClass.Series, MatchBand.Auto, WindBreakerKorea, Declared: new(DeclaredFactKeys.TypeSlug(DeclaredType.Manhwa), [])),
        new("H09 declared manga: the Japanese \"Wind Breaker\"", F("Wind Breaker", Chaps("Wind Breaker", 40)),
            WorkClass.Series, MatchBand.Auto, WindBreakerJapan, Declared: new(DeclaredFactKeys.TypeSlug(DeclaredType.Manga), [])),

        // --- 1.27.0: the live automatic-matching run (2026-09-27), as PUBLIC lookalikes ------------
        new("L01 T: season-renumbered webtoon, chapter-token archives (latest chapter 235, status total 652)",
            F("Tower of God", ChapTokens("Tower of God", 600), "Manhwa"), WorkClass.Series, MatchBand.Auto, TowerOfGod, Vetoes: MatchReason.None),
        new("L02 category hint positive-only: a webtoon under a Manga folder", F("Tower of God", Units(600), "Manga"),
            WorkClass.Series, MatchBand.Auto, TowerOfGod, Vetoes: MatchReason.None),
        new("L03 count by number: six volumes plus six .5 extras", F("Akira", Vols("Akira", 6).Concat(Vols("Akira", 6, ".5"))),
            WorkClass.Series, MatchBand.Auto, Akira, Vetoes: MatchReason.None),
        // The series and its spin-off are named "<Title> - <Subtitle>" / "<Title> ~Subtitle~"; the spin-off also lists the bare
        // "<Title>" as an alias. Both are the shared head: review, the series first (1.26.1 would have auto-linked the spin-off).
        new("L04 V: tilde / dash subtitle heads, spin-off alias", F("Tensei Kizoku no Isekai Boukenroku", Vols("Tensei Kizoku no Isekai Boukenroku", 5)),
            WorkClass.Series, MatchBand.NeedsReview, TenseiKizoku),
        // Like the live run: the long record is on neither page 1 nor page 2 - unmatched, and no "close second" chip.
        new("L05 R: the leading words of a long title", F("Isekai de Cheat Skill", Vols("Isekai de Cheat Skill", 3)), WorkClass.Series, MatchBand.Unmatched,
            Vetoes: MatchReason.None),
        // The archive title extends the folder name, so it is the second search; two related records (the series and its
        // "Girls Side" spin-off) share that whole name before their subtitles - review, the series first.
        new("L06 R: the archives carry the whole long title", F("Isekai de Cheat Skill",
            Vols("Isekai de Cheat Skill wo Te ni Shita Ore wa, Genjitsu Sekai wo mo Musou Suru", 5)), WorkClass.Series, MatchBand.NeedsReview, IsekaiCheatSkill),
        new("L07 one-word title with many look-alike records", F("Kingdom", Vols("Kingdom", 70)), WorkClass.Series, MatchBand.Auto, Kingdom),
        // The webtoon record's alt "Berserk of Gluttony (Webtoon)" scored a false 1.00 once stripped and tied the manga (1.26.1: review).
        new("L11 B trap: another record's alt title carries a (disambiguator)", F("Berserk of Gluttony", Vols("Berserk of Gluttony", 8)), WorkClass.Series,
            MatchBand.Auto, BerserkOfGluttonyComic),
        new("L12 English totals reach the count rule (IZE Press 13+2 volumes, five chapter platforms at 201)",
            F("Solo Leveling", Vols("Solo Leveling", 15), "Manhwa"), WorkClass.Series, MatchBand.Auto, SoloLeveling, Vetoes: MatchReason.None),
        new("L08 author before a plain dash", F("Urasawa Naoki - Monster", Vols("Monster", 18)), WorkClass.Series, MatchBand.Auto, MonsterUrasawa),
        new("L09 author after \"by\" in a collection", F("Shelf", ["Look Back by Fujimoto Tatsuki.cbz", "Akira v01.cbz", "Oyasumi Punpun v01.cbz"]),
            WorkClass.CollectionLeaf, MatchBand.Auto, LookBack, GroupTitle: "Look Back by Fujimoto Tatsuki"),
        new("L10 trailing [Two Words] that is the author", F("Monster [Urasawa Naoki]", Vols("Monster", 18)), WorkClass.Series, MatchBand.Auto, MonsterUrasawa),

        // --- Detector only ---------------------------------------------------------------------
        new("D01 category container", F("Manga", [], depth: 1, subs: [("Berserk", 41), ("Vinland Saga", 12), ("One Piece", 1100)]), WorkClass.CollectionContainer),
        new("D02 franchise container of numbered parts", F("JoJo no Kimyou na Bouken", [],
            subs: [("JoJo no Kimyou na Bouken Part 3 - Stardust Crusaders", 16), ("JoJo no Kimyou na Bouken Part 4 - Diamond wa Kudakenai", 18)]), WorkClass.FranchiseContainer),
        new("D03 wrapper", F("Berserk Collection", [], subs: [("Berserk", 41)]), WorkClass.Wrapper),
        new("D04 unit subfolder", F("Volumes", Vols("Chainsaw Man", 11), depth: 3), WorkClass.UnitSub),
        new("D05 season subfolder", F("Season 2", Units(330), depth: 3), WorkClass.UnitSub),
        new("D06 library root", F("Library", Vols("Berserk", 3), depth: 0), WorkClass.Excluded),
    ];
}
