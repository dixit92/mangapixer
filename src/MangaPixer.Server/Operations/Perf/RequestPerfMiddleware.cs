namespace com.lifepixer.mangapixer.Server.Operations.Perf;

using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Measurement-only request timing (1.46.0 lane M; OFF unless <c>MangaPixer:Perf:RequestTiming=true</c>): every <c>/api</c>
/// response carries <c>Server-Timing: app;dur=..., sql;dur=..., sqln;dur=..., lockw;dur=...</c> (time in the app, time in
/// SQL, SQL command count, time waiting to begin a write transaction) and its route template is aggregated for the admin
/// diagnostics <c>perf</c> block. Durations and counts only.
/// </summary>
public sealed class RequestPerfMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PerfRecorder _recorder;

    public RequestPerfMiddleware(RequestDelegate next, PerfRecorder recorder)
    {
        _next = next;
        _recorder = recorder;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var scope = _recorder.BeginRequest();
        context.Response.OnStarting(() =>
        {
            var app = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context.Response.Headers.Append("Server-Timing", string.Create(CultureInfo.InvariantCulture,
                $"app;dur={app:0.0}, sql;dur={scope.SqlMs:0.0}, sqln;dur={scope.SqlCount}, lockw;dur={scope.LockWaitMs:0.0}"));
            return Task.CompletedTask;
        });
        try
        {
            await _next(context);
        }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)";
            _recorder.EndRequest(scope, context.Request.Method + " " + route, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }
}

public static class PerfServicesExtensions
{
    /// <summary>Registers the recorder (always; disabled unless the config switch is on).</summary>
    public static IServiceCollection AddPerfTiming(this IServiceCollection services, IConfiguration configuration)
    {
        var enabled = configuration.GetValue(PerfRecorder.ConfigKey, false);
        var trackerMarks = configuration.GetValue(PerfRecorder.TrackerMarksKey, false);
        // A factory registration, so the container disposes the recorder with the host (Dispose stops the process-wide hook).
        services.AddSingleton(_ => new PerfRecorder(enabled, trackerMarks));
        return services;
    }

    public const string SkipHostedServicesKey = "MangaPixer:Perf:SkipHostedServices";

    /// <summary>
    /// Measurement only: removes the hosted services whose class names <c>MangaPixer:Perf:SkipHostedServices</c> lists
    /// (comma-separated, e.g. <c>ThumbnailBackfillHostedService,ContentSignatureBackfillHostedService</c>), so a measured
    /// instance can run with one background feature off at a time. Empty by default: nothing is removed.
    /// </summary>
    public static IReadOnlyList<string> SkipHostedServices(this IServiceCollection services, IConfiguration configuration)
    {
        var names = (configuration[SkipHostedServicesKey] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (names.Count == 0)
            return [];
        var removed = services
            .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                        && d.ImplementationType is { } t && names.Contains(t.Name))
            .ToList();
        foreach (var d in removed)
            services.Remove(d);
        return removed.Select(d => d.ImplementationType!.Name).ToList();
    }

    /// <summary>Adds the timing middleware only when the switch is on (no cost otherwise).</summary>
    public static IApplicationBuilder UsePerfTiming(this IApplicationBuilder app)
    {
        var recorder = app.ApplicationServices.GetRequiredService<PerfRecorder>();
        return recorder.Enabled ? app.UseMiddleware<RequestPerfMiddleware>() : app;
    }
}
