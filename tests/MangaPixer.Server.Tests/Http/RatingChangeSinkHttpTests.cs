namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Features.Ratings;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using Xunit;

/// <summary>
/// The 1.53.0 step-0 rating-change seam, through the real rating routes (<c>PUT</c> / <c>DELETE nodes/{id}/rating</c>): a registered
/// <see cref="IRatingChangeSink"/> hears a first rating, a changed one and a clear - each once, after it was saved - and nothing for a
/// PUT of the same score, a clear with no rating there, an invalid score or a node the user cannot open. A sink that throws does not
/// fail the request (still 204), is logged at Warning with event 3412 (ids and the exception type only), and the next sink still runs.
/// </summary>
public sealed class RatingChangeSinkHttpTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnlyTheTrackerScoreSinkIsRegisteredInProduction()
    {
        // 1.53.0 lane T: the tracker scores' sink is the one production sink (it enqueues only for a user who keeps their score in step).
        using var factory = new ApiTokenTestFactory();
        using var scope = factory.Services.CreateScope();
        Assert.IsType<com.lifepixer.mangapixer.Server.Features.Trackers.TrackerScoreSink>(
            Assert.Single(scope.ServiceProvider.GetServices<IRatingChangeSink>()));
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RatingsService>());
    }

    [Fact]
    public async Task ASink_HearsSetChangeAndClear_AfterTheSave_AndNothingWhenNothingChanged()
    {
        var recorder = new RecordingSink();
        using var factory = new ApiTokenTestFactory(configureServices: services => recorder.Register(services));
        var admin = await factory.AdminAsync();
        var (userId, nodeId) = await SeedAsync(factory);

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(admin, "rcs1", 7)).StatusCode);
        // The sink runs after the save: the row it is told about is already stored.
        Assert.Equal(new (long, long, int?)[] { (userId, nodeId, 7) }, recorder.Calls);
        Assert.Equal(new int?[] { 7 }, recorder.StoredScores);

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(admin, "rcs1", 7)).StatusCode); // unchanged
        Assert.Single(recorder.Calls);

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(admin, "rcs1", 3)).StatusCode); // changed
        Assert.Equal((userId, nodeId, (int?)3), recorder.Calls[1]);
        Assert.Equal(3, recorder.StoredScores[1]);

        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(admin, "rcs1", 11)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(admin, "nosuchnode", 5)).StatusCode);
        Assert.Equal(2, recorder.Calls.Count);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/v1/nodes/rcs1/rating")).StatusCode); // cleared
        Assert.Equal((userId, nodeId, (int?)null), recorder.Calls[2]);
        Assert.Null(recorder.StoredScores[2]);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/v1/nodes/rcs1/rating")).StatusCode); // nothing there
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/v1/nodes/nosuchnode/rating")).StatusCode);
        Assert.Equal(3, recorder.Calls.Count);

        Assert.DoesNotContain(factory.Sink.Events, IsSinkFailure);
    }

    [Fact]
    public async Task AThrowingSink_DoesNotFailTheRequest_IsLoggedAtWarning3412_AndTheNextSinkStillRuns()
    {
        var recorder = new RecordingSink();
        using var factory = new ApiTokenTestFactory(configureServices: services =>
        {
            services.AddSingleton<IRatingChangeSink, ThrowingSink>();
            recorder.Register(services);
        });
        var admin = await factory.AdminAsync();
        var (userId, nodeId) = await SeedAsync(factory);

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(admin, "rcs1", 8)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/v1/nodes/rcs1/rating")).StatusCode);

        // The rating was saved and cleared regardless; the second sink heard both.
        Assert.Equal(new (long, long, int?)[] { (userId, nodeId, 8), (userId, nodeId, null) }, recorder.Calls);
        Assert.Null(await StoredScoreAsync(factory, userId, nodeId));

        var failures = factory.Sink.Events.Where(IsSinkFailure).ToList();
        Assert.Equal(2, failures.Count);
        Assert.All(failures, e =>
        {
            Assert.Equal(LogEventLevel.Warning, e.Level);
            Assert.Equal(userId.ToString(System.Globalization.CultureInfo.InvariantCulture), e.Properties["UserId"].ToString());
            Assert.Equal(nodeId.ToString(System.Globalization.CultureInfo.InvariantCulture), e.Properties["NodeId"].ToString());
            Assert.Contains(nameof(InvalidOperationException), e.Properties["ExceptionType"].ToString(), StringComparison.Ordinal);
            // Ids and the exception type only: neither the exception nor its message is logged.
            Assert.Null(e.Exception);
            Assert.DoesNotContain(ThrowingSink.Message, e.RenderMessage(), StringComparison.Ordinal);
        });
        Assert.DoesNotContain(ThrowingSink.Message, factory.CapturedLogText(), StringComparison.Ordinal);
    }

    private static bool IsSinkFailure(LogEvent e) =>
        e.Properties.TryGetValue("EventId", out var value) && value is StructureValue id &&
        id.Properties.Any(p => p.Name == "Id" && p.Value.ToString() == LogEvents.Ratings.RatingSinkFailed.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string nodeId, int score) =>
        client.PutAsJsonAsync($"/api/v1/nodes/{nodeId}/rating", new { score });

    /// <summary>A library with one archive "rcs1"; returns the admin's user id and the archive's node id.</summary>
    private static async Task<(long UserId, long NodeId)> SeedAsync(ApiTokenTestFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var library = new LibraryEntity { PublicId = "rcs-lib", DisplayName = "RCS", RootPath = "/synthetic/rcs", CreatedAt = T0 };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        var node = new CatalogNodeEntity
        {
            PublicId = "rcs1",
            LibraryId = library.Id,
            Kind = (int)CatalogNodeKind.Archive,
            DisplayName = "Akira 01",
            RelativePath = "rcs1",
            PathKey = "rcs1",
            SortKey = SortKey.ForNode(CatalogNodeKind.Archive, "Akira 01"),
            CreatedAt = T0,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        var userId = await db.Users.Where(u => u.NormalizedUserName == "ADMIN").Select(u => u.Id).SingleAsync();
        return (userId, node.Id);
    }

    private static async Task<int?> StoredScoreAsync(ApiTokenTestFactory factory, long userId, long nodeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await db.UserRatings.Where(r => r.UserId == userId && r.CatalogNodeId == nodeId).Select(r => (int?)r.Score).FirstOrDefaultAsync();
    }

    /// <summary>
    /// Records each call and the score stored for (user, node) at that moment, read on a context of its own scope (a new connection), so
    /// it sees only what was already saved.
    /// </summary>
    private sealed class RecordingSink : IRatingChangeSink
    {
        private readonly ConcurrentQueue<(long, long, int?)> _calls = new();
        private readonly ConcurrentQueue<int?> _stored = new();
        private IServiceScopeFactory? _scopes;

        public List<(long UserId, long NodeId, int? Score)> Calls => [.. _calls];

        public List<int?> StoredScores => [.. _stored];

        public void Register(IServiceCollection services) => services.AddSingleton<IRatingChangeSink>(sp =>
        {
            _scopes = sp.GetRequiredService<IServiceScopeFactory>();
            return this;
        });

        public async Task RatingChangedAsync(long userId, long catalogNodeId, int? score, CancellationToken ct)
        {
            _calls.Enqueue((userId, catalogNodeId, score));
            using var scope = _scopes!.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            _stored.Enqueue(await db.UserRatings.AsNoTracking()
                .Where(r => r.UserId == userId && r.CatalogNodeId == catalogNodeId).Select(r => (int?)r.Score).FirstOrDefaultAsync(ct));
        }
    }

    private sealed class ThrowingSink : IRatingChangeSink
    {
        public const string Message = "sink failure carrying Akira 01 at /synthetic/rcs";

        public Task RatingChangedAsync(long userId, long catalogNodeId, int? score, CancellationToken ct) =>
            throw new InvalidOperationException(Message);
    }
}
