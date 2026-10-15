namespace com.lifepixer.mangapixer.Server.Features.VolumeScan;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Volume contents scan registrations (1.43.0: which chapters a held volume file holds). Called once from <c>Program</c>; the lane
/// registers its services here, never in <c>Program.cs</c>.
/// </summary>
public static class VolumeScanServicesExtensions
{
    public static IServiceCollection AddVolumeScan(this IServiceCollection services) => services;
}
