namespace com.lifepixer.mangapixer.Server.Features.Export;

/// <summary>
/// Endpoint marker (1.36.0): the personal access token scheme accepts a POST on an endpoint that carries it. Without it the scheme
/// refuses every method but GET / HEAD (<c>method_not_allowed</c>), whatever the token's scopes. Exactly one action carries it -
/// <see cref="ExportScanController.Scan"/> - and <c>ApiTokenHttpTests.Sweep_*</c> fails if another endpoint gains it. Adding it
/// anywhere else needs owner approval (AGENTS.md, "API tokens reach only the export").
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class TokenWriteAllowedAttribute : Attribute;
