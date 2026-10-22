namespace com.lifepixer.mangapixer.Tests.Server;

using Xunit;

/// <summary>
/// Classes that need a quiet machine run in this collection (1.47.0 lane T). xUnit runs a collection that disables
/// parallelization on its own, after all the parallel collections have finished: nothing else is booting a host, running
/// a worker or burning CPU while its tests run, one class after the other.
/// </summary>
/// <remarks>
/// Use it sparingly - each member adds its own duration to the suite's wall time instead of overlapping with the rest.
/// It is for a test that measures a time (the analytics budget, the alt-title latency guard) or whose result depends on
/// no other host being active (the missing-report conversion test), not for process tests that are already deterministic
/// (the worker-cap test pauses its own worker instead).
/// </remarks>
[CollectionDefinition(SerialCollection.Name, DisableParallelization = true)]
public sealed class SerialCollection
{
    public const string Name = "Serial";
}
