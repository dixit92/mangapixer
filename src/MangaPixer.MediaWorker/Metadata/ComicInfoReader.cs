namespace com.lifepixer.mangapixer.MediaWorker.Metadata;

using System.Globalization;
using System.Text;
using System.Xml;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Archives;

/// <summary>
/// Reads an archive's ComicInfo.xml (1.24.0). Runs ONLY in the worker: the XML is
/// untrusted input, so parsing stays inside the worker's fault-isolation boundary
/// and the server only ever receives the bounded <see cref="ComicInfoPayload"/>.
///
/// Hardening: DTDs are prohibited (no XXE, no entity expansion / billion laughs),
/// there is no resolver, the entry is capped at <see cref="ComicInfoLimits.MaxXmlBytes"/>
/// before decompression and while decompressing, the document is capped at
/// <see cref="ComicInfoLimits.MaxCharactersInDocument"/> characters, comments and
/// processing instructions are ignored, and every mapped value is trimmed,
/// stripped of control characters and length-capped. Unknown elements are skipped.
/// </summary>
public static class ComicInfoReader
{
    public const string EntryFileName = "ComicInfo.xml";

    /// <summary>
    /// Picks the ComicInfo.xml entry: a root-level one wins; otherwise exactly one
    /// nested one is accepted (several nested copies are ambiguous -> none).
    /// AppleDouble / __MACOSX shadows are ignored.
    /// </summary>
    public static ArchiveEntryInfo? Locate(IReadOnlyList<ArchiveEntryInfo> entries)
    {
        var candidates = entries
            .Where(e => !e.IsDirectory && IsComicInfoPath(e.EntryPath))
            .ToList();
        var root = candidates.FirstOrDefault(e => !e.EntryPath.Contains('/') && !e.EntryPath.Contains('\\'));
        if (root is not null)
            return root;
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// Reads ComicInfo from an open archive. Never throws for content problems: every
    /// outcome is a status. Solid archives are skipped WITHOUT decompressing.
    /// </summary>
    public static async Task<ComicInfoOutcome> ReadAsync(
        ArchiveReader reader,
        IReadOnlyList<ArchiveEntryInfo> entries,
        bool isSolid,
        long maxXmlBytes,
        CancellationToken ct)
    {
        var entry = Locate(entries);
        if (entry is null)
            return new ComicInfoOutcome { Status = ComicInfoStatus.Absent };
        if (isSolid)
            return new ComicInfoOutcome { Status = ComicInfoStatus.SkippedSolid };
        if (entry.IsEncrypted)
            return new ComicInfoOutcome { Status = ComicInfoStatus.ReadError };
        if (entry.UncompressedSize > maxXmlBytes)
            return new ComicInfoOutcome { Status = ComicInfoStatus.TooLarge };

        byte[]? bytes;
        try
        {
            bytes = await reader.ReadEntryBoundedAsync(entry.EntryPath, maxXmlBytes, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new ComicInfoOutcome { Status = ComicInfoStatus.ReadError };
        }

        if (bytes is null)
            return new ComicInfoOutcome { Status = ComicInfoStatus.TooLarge };
        return Parse(bytes);
    }

    /// <summary>Parses ComicInfo XML bytes (encoding from BOM / declaration) into a bounded payload.</summary>
    public static ComicInfoOutcome Parse(byte[] xml)
    {
        if (xml.LongLength > ComicInfoLimits.MaxXmlBytes)
            return new ComicInfoOutcome { Status = ComicInfoStatus.TooLarge };

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = ComicInfoLimits.MaxCharactersInDocument,
            MaxCharactersFromEntities = 1024,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CloseInput = true,
        };

        try
        {
            using var stream = new MemoryStream(xml, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            if (reader.MoveToContent() != XmlNodeType.Element
                || !string.Equals(reader.LocalName, "ComicInfo", StringComparison.OrdinalIgnoreCase))
                return new ComicInfoOutcome { Status = ComicInfoStatus.Malformed };

            var builder = new PayloadBuilder();
            if (reader.IsEmptyElement)
                return new ComicInfoOutcome { Status = ComicInfoStatus.Parsed, Payload = builder.Build() };

            reader.ReadStartElement();
            while (reader.MoveToContent() == XmlNodeType.Element)
            {
                var name = reader.LocalName;
                if (builder.IsKnown(name))
                {
                    var value = reader.ReadElementContentAsString();
                    builder.Set(name, value);
                }
                else
                {
                    reader.Skip();
                }
            }

            return new ComicInfoOutcome { Status = ComicInfoStatus.Parsed, Payload = builder.Build() };
        }
        catch (XmlException)
        {
            return new ComicInfoOutcome { Status = ComicInfoStatus.Malformed };
        }
        catch (DecoderFallbackException)
        {
            return new ComicInfoOutcome { Status = ComicInfoStatus.Malformed };
        }
        catch (ArgumentException)
        {
            // Unsupported encoding name in the XML declaration.
            return new ComicInfoOutcome { Status = ComicInfoStatus.Malformed };
        }
    }

    private static bool IsComicInfoPath(string entryPath)
    {
        if (string.IsNullOrEmpty(entryPath))
            return false;
        if (entryPath.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
            || entryPath.Contains("/__MACOSX/", StringComparison.OrdinalIgnoreCase))
            return false;
        var fileName = entryPath.Replace('\\', '/');
        var slash = fileName.LastIndexOf('/');
        if (slash >= 0) fileName = fileName[(slash + 1)..];
        return string.Equals(fileName, EntryFileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Maps known ComicInfo elements (schema v1/v2) to capped, sanitized fields.</summary>
    private sealed class PayloadBuilder
    {
        private static readonly Dictionary<string, string> s_creatorRoles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Writer"] = "writer",
            ["Penciller"] = "penciller",
            ["Inker"] = "inker",
            ["Colorist"] = "colorist",
            ["Letterer"] = "letterer",
            ["CoverArtist"] = "coverArtist",
            ["Editor"] = "editor",
            ["Translator"] = "translator",
        };

        private static readonly HashSet<string> s_known = new(StringComparer.OrdinalIgnoreCase)
        {
            "Series", "Title", "AlternateSeries", "SeriesGroup", "StoryArc", "Number", "Format",
            "AgeRating", "LanguageISO", "Volume", "Count", "Year", "Month", "Summary", "Publisher",
            "Imprint", "Genre", "Tags", "Web", "Manga", "GTIN", "Notes",
            "Writer", "Penciller", "Inker", "Colorist", "Letterer", "CoverArtist", "Editor", "Translator",
        };

        private readonly Dictionary<string, string> _text = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ComicInfoCreator> _creators = [];
        private readonly List<string> _genres = [];
        private readonly List<string> _tags = [];
        private readonly List<string> _web = [];

        public bool IsKnown(string name) => s_known.Contains(name);

        public void Set(string name, string raw)
        {
            if (s_creatorRoles.TryGetValue(name, out var role))
            {
                foreach (var person in SplitList(raw, ','))
                {
                    if (_creators.Count >= ComicInfoPayload.MaxListItems) break;
                    if (!_creators.Any(c => c.Role == role && string.Equals(c.Name, person, StringComparison.OrdinalIgnoreCase)))
                        _creators.Add(new ComicInfoCreator { Name = person, Role = role });
                }
                return;
            }

            switch (name.ToUpperInvariant())
            {
                case "GENRE":
                    AddAll(_genres, SplitList(raw, ','));
                    return;
                case "TAGS":
                    AddAll(_tags, SplitList(raw, ','));
                    return;
                case "WEB":
                    foreach (var url in raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (_web.Count >= ComicInfoPayload.MaxWebUrls) break;
                        if (url.Length <= 512
                            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                            && !_web.Contains(url, StringComparer.Ordinal))
                            _web.Add(url);
                    }
                    return;
                default:
                    // First occurrence wins; duplicates are ignored.
                    _text.TryAdd(name, raw);
                    return;
            }
        }

        public ComicInfoPayload Build() => new()
        {
            Series = Short("Series"),
            Title = Short("Title"),
            AlternateSeries = Short("AlternateSeries"),
            SeriesGroup = Short("SeriesGroup"),
            StoryArc = Short("StoryArc"),
            Number = Code("Number"),
            Format = Code("Format"),
            AgeRating = Code("AgeRating"),
            LanguageIso = Code("LanguageISO"),
            Volume = Int("Volume", 0, 100_000),
            Count = Int("Count", 0, 100_000),
            Year = Int("Year", 1000, 3000),
            Month = Int("Month", 1, 12),
            Summary = Clean(Get("Summary"), ComicInfoPayload.MaxSummaryText, multiline: true),
            Creators = _creators,
            Publisher = Clean(Get("Publisher"), ComicInfoPayload.MaxPublisherText),
            Imprint = Clean(Get("Imprint"), ComicInfoPayload.MaxPublisherText),
            Genres = _genres,
            Tags = _tags,
            WebUrls = _web,
            MangaDirection = Get("Manga")?.Trim().ToUpperInvariant() switch
            {
                "NO" => 0,
                "YES" => 1,
                "YESANDRIGHTTOLEFT" => 2,
                _ => null,
            },
            Gtin = Code("GTIN"),
            Notes = Clean(Get("Notes"), ComicInfoPayload.MaxNotesText, multiline: true),
        };

        private string? Get(string name) => _text.TryGetValue(name, out var v) ? v : null;

        private string? Short(string name) => Clean(Get(name), ComicInfoPayload.MaxShortText);

        private string? Code(string name) => Clean(Get(name), ComicInfoPayload.MaxCodeText);

        private int? Int(string name, int min, int max)
        {
            var raw = Get(name)?.Trim();
            if (raw is null || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return null;
            return value >= min && value <= max ? value : null;
        }

        private static void AddAll(List<string> target, IEnumerable<string> items)
        {
            foreach (var item in items)
            {
                if (target.Count >= ComicInfoPayload.MaxListItems) break;
                if (!target.Contains(item, StringComparer.OrdinalIgnoreCase))
                    target.Add(item);
            }
        }

        private static IEnumerable<string> SplitList(string raw, char separator) =>
            raw.Split(separator)
                .Select(p => Clean(p, ComicInfoPayload.MaxListItemText))
                .Where(p => p is not null)
                .Select(p => p!);
    }

    /// <summary>
    /// Trims, drops control characters (keeping line breaks and tabs only when
    /// <paramref name="multiline"/>), collapses single-line whitespace and caps the
    /// length. Empty results become null.
    /// </summary>
    internal static string? Clean(string? raw, int maxLength, bool multiline = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var sb = new StringBuilder(Math.Min(raw.Length, maxLength + 16));
        foreach (var ch in raw)
        {
            if (ch == '\r')
                continue;
            var c = ch;
            if (c == '\n' || c == '\t')
            {
                if (multiline) { sb.Append(c); continue; }
                c = ' ';
            }
            else if (char.IsControl(c)
                || (char.GetUnicodeCategory(c) == UnicodeCategory.Format && c is not '‍' and not '‌'))
            {
                continue; // control characters, BOMs, bidi overrides
            }

            // Single-line values collapse runs of spaces as they are built.
            if (!multiline && c == ' ' && sb.Length > 0 && sb[^1] == ' ')
                continue;
            sb.Append(c);
        }

        var s = sb.ToString().Trim();
        if (s.Length > maxLength)
        {
            s = s[..maxLength];
            // Do not leave a dangling high surrogate after the cut.
            if (s.Length > 0 && char.IsHighSurrogate(s[^1]))
                s = s[..^1];
            s = s.TrimEnd();
        }
        return s.Length == 0 ? null : s;
    }
}
