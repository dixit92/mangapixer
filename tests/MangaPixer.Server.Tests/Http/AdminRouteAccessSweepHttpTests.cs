namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using com.lifepixer.mangapixer.Server.Features.Export;
using Microsoft.AspNetCore.Authorization;
using Xunit;

/// <summary>
/// 1.48.0 lane A (access invariants, gap 1 + 5): EVERY mapped route under <c>/api/v1/admin</c> and <c>/api/v1/operations</c> (the
/// diagnostics included), and every other route that names the <c>Admin</c> policy, refuses a signed-in non-admin reader with 403 and an
/// anonymous caller with 401, for every method it maps - and names the <c>Admin</c> policy itself, so a new admin route without the policy
/// fails here before anyone can reach it. The export's routes refuse a reader's cookie too. One host for the whole sweep.
/// </summary>
public sealed class AdminRouteAccessSweepHttpTests
{
    private const string AdminPolicy = "Admin";
    private static readonly string[] AdminPrefixes = ["api/v1/admin", "api/v1/operations"];

    [Fact]
    public async Task Sweep_EveryAdminRoute_Is403ForAReader_And401Anonymous_AndNamesTheAdminPolicy()
    {
        await using var factory = new ApiTokenTestFactory();
        var admin = await factory.AdminAsync();
        var reader = await AccessTestUsers.ReaderAsync(factory, admin, "sweepreader", []);
        var anonymous = factory.CreateClient();

        var endpoints = AccessSweepSupport.Endpoints(factory.Services);
        var underPrefix = endpoints.Where(e => AccessSweepSupport.Under(e, AdminPrefixes)).ToList();

        // Statically: every route under the admin prefixes names the Admin policy and opts out of nothing.
        var withoutPolicy = underPrefix
            .Where(e => !AccessSweepSupport.PoliciesOf(e).Contains(AdminPolicy) || e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .SelectMany(e => AccessSweepSupport.MethodsOf(e).Select(m => AccessSweepSupport.Key(m, e)))
            .ToList();
        Assert.True(withoutPolicy.Count == 0, "admin routes without [Authorize(Policy = \"Admin\")] (or with AllowAnonymous):\n" + string.Join("\n", withoutPolicy));

        // Over HTTP: the prefixed routes plus every other route naming the Admin policy (cover choices, volume cover images, ...).
        var adminOnly = endpoints.Where(e => AccessSweepSupport.Under(e, AdminPrefixes) || AccessSweepSupport.PoliciesOf(e).Contains(AdminPolicy)).ToList();
        var swept = 0;
        var failures = new List<string>();
        foreach (var endpoint in adminOnly)
        {
            var url = "/" + AccessSweepSupport.Concrete(endpoint.RoutePattern);
            foreach (var method in AccessSweepSupport.MethodsOf(endpoint))
            {
                using var asReader = await reader.Client.SendAsync(AccessSweepSupport.Request(method, url));
                using var asReaderIncognito = await reader.Incognito.SendAsync(AccessSweepSupport.Request(method, url));
                using var asAnonymous = await anonymous.SendAsync(AccessSweepSupport.Request(method, url));
                swept++;
                if (asReader.StatusCode != HttpStatusCode.Forbidden || asReaderIncognito.StatusCode != HttpStatusCode.Forbidden)
                    failures.Add($"{method} {url}: reader {(int)asReader.StatusCode} / Incognito {(int)asReaderIncognito.StatusCode}, expected 403");
                if (asAnonymous.StatusCode != HttpStatusCode.Unauthorized)
                    failures.Add($"{method} {url}: anonymous {(int)asAnonymous.StatusCode}, expected 401");
            }
        }

        // The audit counted 158 (method, route) pairs under the two prefixes at 1.47.0; the sweep must not quietly shrink.
        var underPrefixRequests = underPrefix.Sum(e => AccessSweepSupport.MethodsOf(e).Count());
        Assert.True(underPrefixRequests >= 158, $"only {underPrefixRequests} admin / operations requests were found");
        Assert.True(swept > underPrefixRequests, $"no Admin-policy route outside the prefixes was swept ({swept})");
        Assert.True(failures.Count == 0, $"{failures.Count} of {swept} admin requests were not refused:\n" + string.Join("\n", failures));

        // The diagnostics (gap 5) by name, so a renamed route cannot drop out of the sweep unnoticed.
        foreach (var path in new[] { "/api/v1/operations/diagnostics", "/api/v1/operations/diagnostics/export" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.Client.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(path)).StatusCode);
        }
    }

    /// <summary>The export is an admin's (cookie) or a token's: a reader's cookie reaches none of it, for every method mapped there.</summary>
    [Fact]
    public async Task Sweep_TheExport_RefusesAReadersCookie()
    {
        await using var factory = new ApiTokenTestFactory();
        var admin = await factory.AdminAsync();
        var reader = await AccessTestUsers.ReaderAsync(factory, admin, "exportreader", []);

        var export = AccessSweepSupport.Endpoints(factory.Services).Where(e => AccessSweepSupport.Under(e, ExportApi.RoutePrefix)).ToList();
        Assert.NotEmpty(export);
        var failures = new List<string>();
        foreach (var endpoint in export)
        {
            var url = "/" + AccessSweepSupport.Concrete(endpoint.RoutePattern);
            foreach (var method in AccessSweepSupport.MethodsOf(endpoint))
            {
                using var response = await reader.Client.SendAsync(AccessSweepSupport.Request(method, url));
                if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized))
                    failures.Add($"{method} {url}: reader {(int)response.StatusCode}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
