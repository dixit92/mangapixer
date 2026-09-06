namespace com.lifepixer.mangaplex.Core;

/// <summary>
/// Product identity constants shared across server, worker, and client.
/// Version values are populated at build time from Version.props.
/// </summary>
public static class ProductIdentity
{
    public const string Name = "MangaPlex";

    /// <summary>
    /// Root namespace/package prefix for all MangaPlex assemblies.
    /// </summary>
    public const string RootNamespace = "com.lifepixer.mangaplex";

    /// <summary>
    /// HTTP API version prefix.
    /// </summary>
    public const string ApiVersionPrefix = "/api/v1";
}
