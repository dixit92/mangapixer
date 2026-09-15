namespace com.lifepixer.mangapixer.Core;

/// <summary>
/// Product identity constants shared across server, worker, and client.
/// Version values are populated at build time from Version.props.
/// </summary>
public static class ProductIdentity
{
    public const string Name = "MangaPixer";

    /// <summary>
    /// Root namespace/package prefix for all MangaPixer assemblies.
    /// </summary>
    public const string RootNamespace = "com.lifepixer.mangapixer";

    /// <summary>
    /// HTTP API version prefix.
    /// </summary>
    public const string ApiVersionPrefix = "/api/v1";
}
