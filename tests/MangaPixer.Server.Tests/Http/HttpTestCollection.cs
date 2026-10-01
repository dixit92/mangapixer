namespace com.lifepixer.mangapixer.Tests.Server.Http;

using Xunit;

/// <summary>
/// Legacy collection name, kept so a class carrying <c>[Collection("HttpSerial")]</c> still compiles and runs.
/// </summary>
/// <remarks>
/// <para>
/// It used to serialize every host-booting class (<c>DisableParallelization = true</c>) because each host boot
/// assigned the process-global Serilog <c>Log.Logger</c>, which log-capturing tests also wrapped. Program.Main now
/// builds a per-host logger and the capturing tests wrap that one (see <c>TestHostLogging</c>), so the reason is
/// gone and no class here uses the collection any more.
/// </para>
/// <para>
/// Classes that still carry it run one after another (members of a collection do), alongside every other
/// collection. New classes should simply omit the attribute; an old class can drop it without any other change.
/// </para>
/// </remarks>
[CollectionDefinition("HttpSerial")]
public sealed class HttpTestCollection
{
}
