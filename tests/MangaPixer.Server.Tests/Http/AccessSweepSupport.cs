namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using System.Text;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Shared support of the endpoint sweeps (1.48.0 lane A, lifted out of <c>ApiTokenHttpTests</c>): every mapped route endpoint of a
/// booted host, its methods, policies and id parameters, a concrete URL with each route parameter filled, and the request a sweep sends.
/// </summary>
internal static class AccessSweepSupport
{
    /// <summary>The header the web client sends while Incognito is on.</summary>
    public const string IncognitoHeader = "X-Incognito";

    /// <summary>Every mapped route endpoint of the host (controllers and minimal endpoints).</summary>
    public static IReadOnlyList<RouteEndpoint> Endpoints(IServiceProvider services) =>
        services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

    /// <summary>The route template without its leading slash, e.g. <c>api/v1/nodes/{nodeId}</c>.</summary>
    public static string RawRoute(RouteEndpoint endpoint) => endpoint.RoutePattern.RawText?.TrimStart('/') ?? string.Empty;

    /// <summary>The HTTP methods the endpoint maps (GET when it names none).</summary>
    public static IEnumerable<string> MethodsOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];

    /// <summary>"METHOD route-template", the key the sweeps list routes by.</summary>
    public static string Key(string method, RouteEndpoint endpoint) => method + " " + RawRoute(endpoint);

    /// <summary>The policies named by the endpoint's <c>[Authorize]</c> data (null for a bare <c>[Authorize]</c>).</summary>
    public static IEnumerable<string?> PoliciesOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy);

    /// <summary>True when the route lies under one of <paramref name="prefixes"/> (no leading slash, compared case-insensitively).</summary>
    public static bool Under(RouteEndpoint endpoint, params string[] prefixes) =>
        prefixes.Any(prefix => RawRoute(endpoint).StartsWith(prefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
            || RawRoute(endpoint).Equals(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>The names of the route's path parameters, in template order.</summary>
    public static IReadOnlyList<string> PathParameters(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.Parameters.Select(p => p.Name).ToList();

    /// <summary>The names of the action's query-bound parameters (explicit <c>[FromQuery]</c> or inferred by <c>[ApiController]</c>).</summary>
    public static IReadOnlyList<string> QueryParameters(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.Parameters
            .Where(p => p.BindingInfo?.BindingSource == BindingSource.Query)
            .Select(p => p.BindingInfo?.BinderModelName ?? p.Name)
            .ToList() ?? [];

    /// <summary>
    /// A query parameter that names a library, node, item or series (<c>libraryId</c>, <c>parentId</c>, <c>ids</c>, ...): any name ending in
    /// <c>Id</c> / <c>Ids</c>, or <c>ids</c> itself. A new route taking such a parameter joins the id sweep automatically.
    /// </summary>
    public static bool IsIdQueryParameter(string name) =>
        name.Equals("ids", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Id", StringComparison.Ordinal)
        || name.EndsWith("Ids", StringComparison.Ordinal);

    /// <summary>The route pattern with every parameter filled by <paramref name="fill"/> (default: the placeholder <c>"1"</c>).</summary>
    public static string Concrete(RoutePattern pattern, Func<string, string>? fill = null)
    {
        var segments = pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart parameter => Uri.EscapeDataString(fill?.Invoke(parameter.Name) ?? "1"),
            _ => string.Empty,
        })));
        return string.Join("/", segments);
    }

    /// <summary>The sweep's request: an empty JSON object as the body of every method that carries one; Incognito on request.</summary>
    public static HttpRequestMessage Request(string method, string url, bool incognito = false)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is not ("GET" or "HEAD" or "DELETE" or "OPTIONS"))
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        if (incognito)
            request.Headers.Add(IncognitoHeader, "1");
        return request;
    }
}

/// <summary>A signed-in non-admin reader: one session without and one with Incognito (two logins, each with its CSRF header).</summary>
internal sealed record AccessReader(HttpClient Client, HttpClient Incognito, long UserId, string UserName);

/// <summary>
/// The shared "create a reader, grant libraries, log in, optional Incognito" helper (1.48.0 lane A). The older private copies in about a
/// dozen HTTP test classes are kept as they are (listed in the lane note); new tests use this one.
/// </summary>
internal static class AccessTestUsers
{
    public const string ReaderPassword = "ReaderPassword123!";

    /// <summary>
    /// An activated, signed-in reader (created by <paramref name="admin"/> through the admin API, activated with its one-time link) granted
    /// <paramref name="grantLibraryIds"/> (internal ids), with <paramref name="privateLibraryIds"/> marked Private for them.
    /// </summary>
    public static async Task<AccessReader> ReaderAsync(
        WebApplicationFactory<Program> factory, HttpClient admin, string username, IEnumerable<long> grantLibraryIds,
        IEnumerable<long>? privateLibraryIds = null)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest { Username = username, IsAdmin = false });
        created.EnsureSuccessStatusCode();
        var url = (await created.Content.ReadFromJsonAsync<CreateUserResponse>(TestJson.Web))!.ActivationUrl!;
        var token = Uri.UnescapeDataString(url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/activate",
            new ActivateAccountRequest { Token = token, Password = ReaderPassword })).EnsureSuccessStatusCode();

        long userId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            userId = await db.Users.Where(u => u.NormalizedUserName == username.ToUpperInvariant()).Select(u => u.Id).SingleAsync();
            foreach (var library in grantLibraryIds.Distinct())
                db.LibraryGrants.Add(new LibraryGrantEntity { UserId = userId, LibraryId = library, GrantedAt = DateTimeOffset.UtcNow });
            foreach (var library in (privateLibraryIds ?? []).Distinct())
                db.PrivateLibraries.Add(new PrivateLibraryEntity { UserId = userId, LibraryId = library, MarkedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var client = await LoginAsync(factory, username, ReaderPassword);
        var incognito = await LoginAsync(factory, username, ReaderPassword);
        incognito.DefaultRequestHeaders.Add(AccessSweepSupport.IncognitoHeader, "1");
        return new AccessReader(client, incognito, userId, username);
    }

    /// <summary>A new signed-in session for <paramref name="username"/> with its CSRF header.</summary>
    public static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory, string username, string password)
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = password })).EnsureSuccessStatusCode();
        var csrf = await (await client.GetAsync("/api/v1/auth/csrf")).Content.ReadFromJsonAsync<CsrfTokenDto>();
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
        return client;
    }
}
