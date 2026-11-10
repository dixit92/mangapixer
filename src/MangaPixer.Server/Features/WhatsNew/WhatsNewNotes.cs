namespace com.lifepixer.mangapixer.Server.Features.WhatsNew;

using System.Reflection;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Logging;

/// <summary>
/// The release notes of the running build (1.53.0 "What's new"): the repository's <c>CHANGELOG.md</c>, embedded in the server assembly at
/// build time (<c>MangaPixer.Server.csproj</c>, logical name <see cref="ResourceName"/>), so the notes always match the binary, work offline
/// and need no request. Parsed once, on first use, and kept. A missing or malformed file yields no sections (logged at Warning), never an
/// error to the caller. Singleton.
///
/// The running version is the assembly's <c>InformationalVersion</c> (from <c>Version.props</c>), as <c>/api/v1/system/info</c> reports it.
/// Under the <c>Testing</c> environment only, <c>WhatsNew:ChangelogPath</c> (a file) and <c>WhatsNew:RunningVersion</c> replace the two, so
/// HTTP tests run on a synthetic CHANGELOG; any other environment ignores them.
/// </summary>
public sealed class WhatsNewNotes
{
    public const string ResourceName = "CHANGELOG.md";

    private readonly Lazy<IReadOnlyList<WhatsNewSectionDto>> _sections;
    private readonly ILogger<WhatsNewNotes> _logger;
    private readonly string? _changelogPath;

    public WhatsNewNotes(IConfiguration configuration, IHostEnvironment environment, ILogger<WhatsNewNotes> logger)
    {
        _logger = logger;
        var testing = environment.IsEnvironment("Testing");
        _changelogPath = testing ? configuration["WhatsNew:ChangelogPath"] : null;
        var running = (testing ? configuration["WhatsNew:RunningVersion"] : null) ?? AssemblyVersion();
        Current = WhatsNewSelection.CoreText(running);
        _sections = new Lazy<IReadOnlyList<WhatsNewSectionDto>>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The running version's <c>major.minor.patch</c>, or null when it cannot be read.</summary>
    public string? Current { get; }

    /// <summary>Every released section of the notes, in file order (empty when the file is missing or malformed).</summary>
    public IReadOnlyList<WhatsNewSectionDto> Sections => _sections.Value;

    /// <summary>What an admin who last saw <paramref name="lastSeenVersion"/> gets (see <see cref="WhatsNewSelection"/>).</summary>
    public WhatsNewDto For(string? lastSeenVersion, bool currentOnly) => new()
    {
        Current = Current ?? string.Empty,
        Sections = WhatsNewSelection.Select(Sections, Current, lastSeenVersion, currentOnly),
    };

    /// <summary>The embedded notes as text, or null when the resource is missing.</summary>
    public static string? ReadEmbedded()
    {
        using var stream = typeof(WhatsNewNotes).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private IReadOnlyList<WhatsNewSectionDto> Load()
    {
        try
        {
            var text = _changelogPath is not null ? File.ReadAllText(_changelogPath) : ReadEmbedded();
            if (text is null)
            {
                _logger.LogWarning(LogEvents.Administration.WhatsNewNotesUnreadable, "What's new: the release notes are missing ({Error})", "missing");
                return [];
            }

            var result = ChangelogParser.Parse(text);
            if (result.Error is not null)
            {
                _logger.LogWarning(LogEvents.Administration.WhatsNewNotesUnreadable, "What's new: the release notes could not be read ({Error})", result.Error);
                return [];
            }

            _logger.LogDebug(LogEvents.Administration.WhatsNewNotesParsed, "What's new: {Count} release sections read", result.Sections.Count);
            return result.Sections;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(LogEvents.Administration.WhatsNewNotesUnreadable, "What's new: the release notes could not be read ({Error})", ex.GetType().Name);
            return [];
        }
    }

    private static string? AssemblyVersion() =>
        typeof(WhatsNewNotes).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(WhatsNewNotes).Assembly.GetName().Version?.ToString();
}
