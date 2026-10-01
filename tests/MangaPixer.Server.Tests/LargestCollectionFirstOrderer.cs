[assembly: Xunit.TestCollectionOrderer("com.lifepixer.mangapixer.Tests.Server.LargestCollectionFirstOrderer", "MangaPixer.Server.Tests")]

namespace com.lifepixer.mangapixer.Tests.Server;

using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

/// <summary>
/// Starts the test classes with the most test methods first. xUnit runs the tests of one class strictly one after
/// another (a class is its own collection), so a 40-test class that happens to sort late in the alphabet starts
/// near the end of the run and its sequential chain becomes a tail in which most cores sit idle. Starting the long
/// chains first (longest-processing-time-first scheduling) lets the short classes fill the gaps. The order only
/// affects when a class starts, never what runs; ties keep the name order so the schedule is deterministic.
/// </summary>
public sealed class LargestCollectionFirstOrderer : ITestCollectionOrderer
{
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        testCollections
            .OrderByDescending(Weight)
            .ThenBy(c => c.DisplayName, StringComparer.Ordinal);

    private static int Weight(ITestCollection collection)
    {
        var assembly = collection.TestAssembly.Assembly as IReflectionAssemblyInfo;
        if (assembly is null)
            return 0;

        // A per-class collection is named "Test collection for <full class name>"; a [CollectionDefinition]
        // collection counts every class that joined it.
        var methods = 0;
        foreach (var type in assembly.Assembly.GetTypes())
        {
            var member = type.GetCustomAttributesData()
                .FirstOrDefault(a => a.AttributeType == typeof(CollectionAttribute))?.ConstructorArguments[0].Value as string;
            var belongs = member is null
                ? collection.DisplayName == "Test collection for " + type.FullName
                : collection.DisplayName == member;
            if (!belongs)
                continue;
            methods += type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Count(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any());
        }
        return methods;
    }
}
