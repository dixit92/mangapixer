namespace com.lifepixer.mangapixer.TestSupport.Fixtures;

using System.IO.Compression;
using System.Security;
using System.Text;

/// <summary>
/// Synthetic ComicInfo.xml fixtures (1.24.0 metadata). Every value is invented;
/// no real series, file or collection data.
/// </summary>
public static class ComicInfoFixtures
{
    /// <summary>A complete, well-formed ComicInfo v2 document with synthetic values.</summary>
    public static string SampleXml(
        string series = "Synthetic Saga",
        string number = "3",
        int volume = 1,
        string title = "The Third Step",
        int year = 2021,
        string? web = "https://www.mangaupdates.com/series/abc123/synthetic-saga")
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<ComicInfo xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n");
        sb.Append($"  <Title>{Esc(title)}</Title>\n");
        sb.Append($"  <Series>{Esc(series)}</Series>\n");
        sb.Append($"  <Number>{Esc(number)}</Number>\n");
        sb.Append("  <Count>12</Count>\n");
        sb.Append($"  <Volume>{volume}</Volume>\n");
        sb.Append("  <Summary>A synthetic summary.\nSecond line.</Summary>\n");
        sb.Append($"  <Year>{year}</Year>\n");
        sb.Append("  <Month>4</Month>\n");
        sb.Append("  <Writer>Test Writer, Second Writer</Writer>\n");
        sb.Append("  <Penciller>Test Artist</Penciller>\n");
        sb.Append("  <Publisher>Synthetic Press</Publisher>\n");
        sb.Append("  <Genre>Action, Fantasy</Genre>\n");
        sb.Append("  <Tags>tag-one, tag-two</Tags>\n");
        if (web is not null)
            sb.Append($"  <Web>{Esc(web)}</Web>\n");
        sb.Append("  <LanguageISO>en</LanguageISO>\n");
        sb.Append("  <Manga>YesAndRightToLeft</Manga>\n");
        sb.Append("  <Pages><Page Image=\"0\" Type=\"FrontCover\" /></Pages>\n");
        sb.Append("</ComicInfo>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Creates a ZIP (.cbz) holding tiny synthetic pages plus a ComicInfo.xml at
    /// <paramref name="comicInfoEntryPath"/> (root by default). Pass null XML for
    /// an archive without ComicInfo.
    /// </summary>
    public static string CreateZipWithComicInfo(
        string outputDir,
        string name,
        string? xml,
        string comicInfoEntryPath = "ComicInfo.xml",
        int pages = 2)
    {
        Directory.CreateDirectory(outputDir);
        var zipPath = Path.Combine(outputDir, name);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        for (var i = 1; i <= pages; i++)
        {
            var entry = zip.CreateEntry($"page{i:D3}.png");
            using var s = entry.Open();
            s.Write(SyntheticImages.MinimalPng);
        }
        if (xml is not null)
        {
            var entry = zip.CreateEntry(comicInfoEntryPath);
            using var s = entry.Open();
            s.Write(Encoding.UTF8.GetBytes(xml));
        }
        return zipPath;
    }

    /// <summary>Creates a ZIP whose ComicInfo.xml entry holds raw bytes (encodings, oversize, garbage).</summary>
    public static string CreateZipWithComicInfoBytes(string outputDir, string name, byte[] comicInfoBytes)
    {
        Directory.CreateDirectory(outputDir);
        var zipPath = Path.Combine(outputDir, name);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var page = zip.CreateEntry("page001.png");
        using (var s = page.Open()) s.Write(SyntheticImages.MinimalPng);
        var entry = zip.CreateEntry("ComicInfo.xml");
        using (var s = entry.Open()) s.Write(comicInfoBytes);
        return zipPath;
    }

    private static string Esc(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
