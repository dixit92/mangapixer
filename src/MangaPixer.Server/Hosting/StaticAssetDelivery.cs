namespace com.lifepixer.mangapixer.Server.Hosting;

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

/// <summary>
/// Delivery of the web app's files from <c>wwwroot/</c> (1.46.0 lane D): the default document, the static files and the SPA fallback,
/// with
/// <list type="bullet">
/// <item><description><b>precompressed variants</b>: the web build (<c>web/scripts/precompress.mjs</c>, run by <c>npm run build</c>)
/// writes <c>&lt;file&gt;.br</c> and <c>&lt;file&gt;.gz</c> next to every compressible file. A request whose <c>Accept-Encoding</c>
/// allows one of them gets that file's bytes with <c>Content-Encoding</c>, the ORIGINAL file's <c>Content-Type</c> and its own
/// <c>ETag</c> / length; every response for a file that has variants says <c>Vary: Accept-Encoding</c>. Nothing is compressed per
/// request; a file without variants (images, fonts, a build made without the script) is sent as before. A variant older than its file
/// is ignored, so a stale copy is never served. A direct request for a <c>.br</c> / <c>.gz</c> path is a 404, as before (no such files
/// existed).</description></item>
/// <item><description><b>cache headers</b>: content-hashed build output (<c>main-ABCD1234.js</c>, <c>media/font-ABCD1234.woff2</c> - a
/// new build gives a changed file a new name) is <c>public, max-age=31536000, immutable</c>; <c>index.html</c> (the default document
/// and the SPA fallback) is <c>no-cache</c>, so a browser always revalidates it and picks up a new build's bundle names. Other files
/// (<c>favicon.ico</c>, <c>manifest.webmanifest</c>, <c>assets/</c>) keep no <c>Cache-Control</c>, as before.</description></item>
/// </list>
/// API responses are not touched: the SPA fallback answers <c>/api</c> paths with a bare 404 as before, and the static file middleware
/// serves only files that exist in <c>wwwroot/</c>.
/// </summary>
public static class StaticAssetDelivery
{
    /// <summary>Cache-Control of a content-hashed file.</summary>
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    /// <summary>Cache-Control of <c>index.html</c>: may be stored, but revalidated on every use.</summary>
    public const string IndexCacheControl = "no-cache";

    private const string IndexFile = "index.html";

    /// <summary>The variants the web build writes, in the server's order of preference, with their file suffix.</summary>
    private static readonly (string Encoding, string Suffix)[] Variants = [("br", ".br"), ("gzip", ".gz")];

    /// <summary>HttpContext item: the encoding of the variant the request was rewritten to.</summary>
    private const string EncodingItem = "MangaPixer.StaticAsset.Encoding";

    /// <summary>HttpContext item: the file the client asked for has precompressed variants (the response varies by Accept-Encoding).</summary>
    private const string VariesItem = "MangaPixer.StaticAsset.Varies";

    /// <summary>HttpContext item: the request path before the rewrite to a variant.</summary>
    private const string OriginalPathItem = "MangaPixer.StaticAsset.OriginalPath";

    /// <summary>
    /// A content-hashed build file: Angular's <c>outputHashing: all</c> names it <c>&lt;name&gt;-&lt;8 upper-case letters or
    /// digits&gt;.&lt;ext&gt;</c> (bundles and chunks at the top level, fonts and images under <c>media/</c>). <c>assets/</c> is copied
    /// verbatim (no hash) and never counts.
    /// </summary>
    private static readonly Regex HashedName = new(@"^/(?:media/)?[^/]+-[A-Z0-9]{8}\.[A-Za-z0-9]+$", RegexOptions.CultureInvariant);

    /// <summary>Whether a request path names a content-hashed build file (see <see cref="HashedName"/>).</summary>
    public static bool IsContentHashed(PathString path) => path.HasValue && HashedName.IsMatch(path.Value!);

    /// <summary>
    /// Serves the web app from <paramref name="wwwrootPath"/>: default document, static files (with precompressed variants and cache
    /// headers) and the SPA fallback to <c>index.html</c> for every non-API, non-file route.
    /// </summary>
    public static WebApplication UseMangaPixerWebApp(this WebApplication app, string wwwrootPath)
    {
        var files = new PhysicalFileProvider(wwwrootPath);
        var contentTypes = new FileExtensionContentTypeProvider();

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.Use((context, next) => SelectVariant(context, next, files));
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = new VariantContentTypeProvider(contentTypes),
            OnPrepareResponse = response => ApplyHeaders(response.Context),
        });

        // SPA fallback - serve index.html for non-API, non-file routes.
        app.MapFallback(context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = 404;
                return Task.CompletedTask;
            }

            var index = files.GetFileInfo(IndexFile);
            if (index.Exists && !index.IsDirectory)
                return SendIndexAsync(context, files, index);

            context.Response.StatusCode = 404;
            return Task.CompletedTask;
        });
        return app;
    }

    /// <summary>
    /// Before the static file middleware: a GET / HEAD for a file with precompressed variants is marked as varying, and rewritten to the
    /// variant the client accepts (if any). A direct request for a variant's own path is a 404.
    /// </summary>
    private static Task SelectVariant(HttpContext context, RequestDelegate next, IFileProvider files)
    {
        var request = context.Request;
        if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) || !request.Path.HasValue
            || request.Path.StartsWithSegments("/api"))
            return next(context);

        var path = request.Path.Value!;
        if (Variants.Any(v => path.EndsWith(v.Suffix, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        var file = files.GetFileInfo(path);
        if (!file.Exists || file.IsDirectory)
            return next(context);

        var available = AvailableVariants(files, path, file);
        if (available.Count == 0)
            return next(context);

        context.Items[VariesItem] = true;
        var chosen = Negotiate(request.Headers.AcceptEncoding, available);
        if (chosen is { } variant)
        {
            context.Items[EncodingItem] = variant.Encoding;
            context.Items[OriginalPathItem] = request.Path;
            request.Path = new PathString(path + variant.Suffix);
        }
        return next(context);
    }

    /// <summary>The variants present next to <paramref name="file"/> and not older than it, in preference order.</summary>
    private static List<(string Encoding, string Suffix)> AvailableVariants(IFileProvider files, string path, IFileInfo file)
    {
        var available = new List<(string Encoding, string Suffix)>(Variants.Length);
        foreach (var variant in Variants)
        {
            var candidate = files.GetFileInfo(path + variant.Suffix);
            if (candidate.Exists && !candidate.IsDirectory && candidate.LastModified >= file.LastModified)
                available.Add(variant);
        }
        return available;
    }

    /// <summary>
    /// The variant to send for an <c>Accept-Encoding</c> header, or null for the file itself: the highest quality value above zero among
    /// the available encodings (an encoding not named takes the <c>*</c> entry's value, else 0); a tie goes to the server's order (Brotli
    /// first). No header, or one that allows none of them, means the file itself.
    /// </summary>
    internal static (string Encoding, string Suffix)? Negotiate(
        Microsoft.Extensions.Primitives.StringValues acceptEncoding, IReadOnlyList<(string Encoding, string Suffix)> available)
    {
        if (acceptEncoding.Count == 0
            || !StringWithQualityHeaderValue.TryParseList(acceptEncoding, out var entries) || entries.Count == 0)
            return null;

        double? Quality(string encoding)
        {
            var named = entries.FirstOrDefault(e => string.Equals(e.Value.Value, encoding, StringComparison.OrdinalIgnoreCase));
            if (named is not null)
                return named.Quality ?? 1.0;
            var any = entries.FirstOrDefault(e => e.Value.Value == "*");
            return any is null ? null : any.Quality ?? 1.0;
        }

        (string Encoding, string Suffix)? best = null;
        var bestQuality = 0.0;
        foreach (var variant in available)
        {
            var q = Quality(variant.Encoding) ?? 0.0;
            if (q > bestQuality)
            {
                best = variant;
                bestQuality = q;
            }
        }
        return best;
    }

    /// <summary>Response headers of a static file (called by the static file middleware for 200, 206, 304 and HEAD answers).</summary>
    private static void ApplyHeaders(HttpContext context)
    {
        var headers = context.Response.Headers;
        if (context.Items.TryGetValue(EncodingItem, out var encoding) && encoding is string name)
            headers.ContentEncoding = name;
        if (context.Items.ContainsKey(VariesItem))
            headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);

        var requested = context.Items.TryGetValue(OriginalPathItem, out var original) && original is PathString p ? p : context.Request.Path;
        if (IsContentHashed(requested))
            headers.CacheControl = ImmutableCacheControl;
        else if (string.Equals(requested.Value, "/" + IndexFile, StringComparison.OrdinalIgnoreCase))
            headers.CacheControl = IndexCacheControl;
    }

    /// <summary>The SPA fallback's <c>index.html</c>: <c>no-cache</c>, and a precompressed variant when the client accepts one.</summary>
    private static Task SendIndexAsync(HttpContext context, IFileProvider files, IFileInfo index)
    {
        var response = context.Response;
        var send = index;
        var available = AvailableVariants(files, "/" + IndexFile, index);
        if (available.Count > 0)
        {
            response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
            if (Negotiate(context.Request.Headers.AcceptEncoding, available) is { } variant)
            {
                send = files.GetFileInfo("/" + IndexFile + variant.Suffix);
                response.Headers.ContentEncoding = variant.Encoding;
            }
        }
        response.Headers.CacheControl = IndexCacheControl;
        response.ContentType = "text/html";
        return response.SendFileAsync(send);
    }

    /// <summary>
    /// Content types by the file a variant stands for (<c>main-X.js.br</c> is <c>text/javascript</c>). Only rewritten requests reach a
    /// variant's path (<see cref="SelectVariant"/> answers direct ones with a 404).
    /// </summary>
    private sealed class VariantContentTypeProvider(FileExtensionContentTypeProvider inner) : IContentTypeProvider
    {
        public bool TryGetContentType(string subpath, out string contentType)
        {
            foreach (var variant in Variants)
            {
                if (subpath.EndsWith(variant.Suffix, StringComparison.OrdinalIgnoreCase))
                    return inner.TryGetContentType(subpath[..^variant.Suffix.Length], out contentType!);
            }
            return inner.TryGetContentType(subpath, out contentType!);
        }
    }
}
