namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.Gcd;

using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// The optional details of a CHOSEN Grand Comics Database candidate (1.32.0): its publisher's name and its first issue (cover
/// thumbnail for identification, credits, pages, ISBN). Every request goes through <see cref="MetadataGateway.DetailCallAsync{T}"/>
/// - the same gates, budget and 25-an-hour bucket as a search. Best effort: when the bucket is empty, GCD is slow or failing, the
/// record is used as it is (a missing publisher or cover never fails Identify or a link); a switch that was turned off still
/// refuses as everywhere.
/// </summary>
public sealed class GcdDetails
{
    private readonly MetadataGateway _gateway;
    private readonly MetadataProviderRegistry _providers;

    public GcdDetails(MetadataGateway gateway, MetadataProviderRegistry providers)
    {
        _gateway = gateway;
        _providers = providers;
    }

    private GcdProvider? Provider => _providers.Find(GcdMapping.ProviderId) as GcdProvider;

    /// <summary>The record with its publisher's name (one request when the name is not known yet).</summary>
    public async Task<ProviderSeriesRecord> WithPublisherAsync(ProviderSeriesRecord record, long libraryId, MetadataCallContext? call, CancellationToken ct)
    {
        if (record.Provider != GcdMapping.ProviderId || record.Publishers.Count > 0 || Provider is not { } provider
            || GcdExtra.Read(record.ExtraJson)?.PublisherId is not { } publisherId)
            return record;
        var name = provider.KnownPublisherName(publisherId)
            ?? await BestEffortAsync(() => _gateway.DetailCallAsync(GcdMapping.ProviderId, "publisher", libraryId,
                c => provider.GetPublisherNameAsync(publisherId, c), call, ct));
        return name is null ? record : record with { Publishers = [new MetadataJson.Publisher(name, "original")] };
    }

    /// <summary>The first issue of a record (one request), or null when there is none or it cannot be read now.</summary>
    public async Task<GcdIssueDetail?> FirstIssueAsync(ProviderSeriesRecord record, long libraryId, MetadataCallContext? call, CancellationToken ct)
    {
        if (record.Provider != GcdMapping.ProviderId || Provider is not { } provider
            || GcdExtra.Read(record.ExtraJson)?.FirstIssueId is not { } issueId)
            return null;
        return await BestEffortAsync(() => _gateway.DetailCallAsync(GcdMapping.ProviderId, "issue", libraryId,
            c => provider.GetIssueAsync(issueId, c), call, ct));
    }

    /// <summary>The record with what its first issue adds: the credited creators, and the indicia publisher when no publisher is known.</summary>
    public static ProviderSeriesRecord WithIssue(ProviderSeriesRecord record, GcdIssueDetail? issue)
    {
        if (issue is null)
            return record;
        var result = record;
        if (record.Creators.Count == 0 && issue.Creators.Count > 0)
            result = result with { Creators = issue.Creators };
        if (record.Publishers.Count == 0 && issue.IndiciaPublisher is { } indicia)
            result = result with { Publishers = [new MetadataJson.Publisher(indicia, "original")] };
        return result;
    }

    /// <summary>Switch refusals propagate (nothing may be sent); everything else - busy, backoff, budget, a GCD failure - reads as "no detail".</summary>
    private static async Task<T?> BestEffortAsync<T>(Func<Task<T?>> call) where T : class
    {
        try
        {
            return await call();
        }
        catch (MetadataGatewayException ex) when (!IsSwitchedOff(ex))
        {
            return null;
        }
    }

    internal static bool IsSwitchedOff(MetadataGatewayException ex) => ex.Code is
        "metadata_network_disabled" or "metadata_disabled" or "library_metadata_disabled" or "automatic_off" or "provider_not_allowed";
}
