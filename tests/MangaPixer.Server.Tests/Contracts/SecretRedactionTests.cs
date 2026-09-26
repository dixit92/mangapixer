namespace com.lifepixer.mangapixer.Tests.Server.Contracts;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Operations;
using Xunit;

/// <summary>
/// A record's compiler-generated <c>ToString</c> prints every property, so a
/// request or response record logged at Debug would print a password or an
/// activation link (1.24.1). Every record in the Core and Server assemblies with
/// a secret-named string property must print it redacted.
/// </summary>
public sealed class SecretRedactionTests
{
    // Password, CurrentPassword, TemporaryPassword, Token, ActivationUrl, ...
    // ImageToken is an opaque cache handle for a provider cover, not a credential.
    private static readonly Regex SecretName = new("(Password|Secret|Token|ActivationUrl)$", RegexOptions.CultureInvariant);

    private static IReadOnlyList<Type> SecretBearingRecordTypes() =>
        new[] { typeof(LoginRequest).Assembly, typeof(UpdateBackupSettingsRequest).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                        && t.GetMethod("<Clone>$") is not null
                        && SecretProperties(t).Any())
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    public static TheoryData<Type> SecretBearingRecords() => new(SecretBearingRecordTypes());

    [Fact]
    public void KnownSecretBearingRecordsAreCovered()
    {
        var types = SecretBearingRecordTypes().ToHashSet();
        Assert.Contains(typeof(LoginRequest), types);
        Assert.Contains(typeof(SetupRequest), types);
        Assert.Contains(typeof(ChangePasswordRequest), types);
        Assert.Contains(typeof(ActivateAccountRequest), types);
        Assert.Contains(typeof(ReissueActivationResponse), types);
        Assert.Contains(typeof(UpdateBackupSettingsRequest), types);
    }

    [Theory]
    [MemberData(nameof(SecretBearingRecords))]
    public void ToString_NeverPrintsSecretValues(Type type)
    {
        var instance = RuntimeHelpers.GetUninitializedObject(type);
        var secrets = new List<string>();
        foreach (var property in SecretProperties(type))
        {
            var value = "S3CRET-" + property.Name;
            property.SetValue(instance, value);
            secrets.Add(value);
        }

        var printed = instance.ToString()!;

        foreach (var secret in secrets)
            Assert.DoesNotContain(secret, printed, StringComparison.Ordinal);
        Assert.Contains("[redacted]", printed, StringComparison.Ordinal);
    }

    private static IEnumerable<PropertyInfo> SecretProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanWrite && p.Name != "ImageToken" && SecretName.IsMatch(p.Name));
}
