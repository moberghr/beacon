using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A record's compiler-generated <c>ToString()</c> prints every property, so a host's request logging (a MediatR
/// behaviour that logs the request, a structured logger that destructures it) would print the secrets a record carries.
/// Every shipped record (class or struct) with a secret-bearing property (a string, a byte array, a list of key-value
/// pairs or a query string) prints it redacted.
/// </summary>
public sealed partial class SecretRecordPrintTests
{
    private const string Sentinel = "sentinel-value-9f3c2a";

    // Members whose name looks secret but that never hold one, each with the reason.
    private static readonly Dictionary<string, string> NotSecret = new(StringComparer.Ordinal)
    {
        ["Beacon.Core.Notifications.MaskedRecipientSecrets.Destination"] = "the masked form shown to admins",
        ["Beacon.Core.Notifications.MaskedRecipientSecrets.HeadersJson"] = "the masked form shown to admins",
        ["Beacon.Core.Handlers.Recipients.RecipientEntry.Destination"] = "the masked form shown to admins",
        ["Beacon.Core.Handlers.Recipients.RecipientEntry.HeadersJson"] = "the masked form shown to admins",
        ["Beacon.Core.Models.Approvals.RecipientDestinationView.Destination"] = "the masked form shown to reviewers",
        ["Beacon.Core.Models.DataQuality.DataContractRecipientData.Destination"] = "always null",
        ["Beacon.Core.Handlers.Subscriptions.SubscriptionDetailRecipient.Destination"] = "always null",
        ["Beacon.Core.HostData.HostConnectionReference.HostConnectionStringName"] = "the name of a configuration entry, not its value",
        ["Beacon.Core.Models.Approvals.LlmConnectionView.Endpoint"] = "the scheme, host and port only; the path shows only as a fingerprint"
    };

    [Test]
    public void EverySecretBearingRecord_PrintsItsSecretsRedacted()
    {
        var offenders = new List<string>();
        var checkedProperties = 0;

        foreach (var type in ShippedRecordTypes())
        {
            foreach (var property in SecretProperties(type))
            {
                if (NotSecret.ContainsKey($"{type.FullName}.{property.Name}"))
                {
                    continue;
                }

                checkedProperties++;
                var printed = PrintWith(type, property);
                if (printed == null || printed.Contains(Sentinel, StringComparison.Ordinal))
                {
                    offenders.Add($"{type.FullName}.{property.Name}");
                }
            }
        }

        checkedProperties.Should().BeGreaterThan(10, "the scan must reach the secret-bearing records");
        string.Join(Environment.NewLine, offenders).Should().BeEmpty("a record's ToString must not print a secret");
    }

    [Test]
    public void ARedactedRecord_StillPrintsItsOrdinaryMembers()
    {
        var command = new Beacon.Core.Handlers.Recipients.CreateRecipientCommand("Ops channel", null, Sentinel, 1, Sentinel, null);

        var printed = command.ToString();

        printed.Should().Contain("Ops channel").And.NotContain(Sentinel);
    }

    [GeneratedRegex(
        "password|secret|apikey|api_key|accesstoken|sessiontoken|refreshtoken|^token$|plaintextkey|connectionstring|connectiondata|privatekey|headersjson|credential|^destination$|^storeddestination$|^storedheadersjson$|endpoint$|accesskeyid|^headers$|^formfields$|^jsonbody$|^querystring$|bearer|jwt|^otp$|passcode|^pat$|^authorization$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    private static IEnumerable<Type> ShippedRecordTypes()
    {
        return ShippedAssemblies()
            .SelectMany(x => LoadableTypes(x))
            .Where(x => !x.IsAbstract)
            .Where(x => !x.ContainsGenericParameters)
            .Where(x => IsRecordClass(x) || IsRecordStruct(x));
    }

    private static IEnumerable<PropertyInfo> SecretProperties(Type type)
    {
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(x => TrySecretValue(x.PropertyType) != null)
            .Where(x => x.GetIndexParameters().Length == 0)
            .Where(x => SecretName().IsMatch(x.Name))
            .Where(x => BackingField(x) != null);
    }

    private static bool IsRecordClass(Type type)
    {
        return type.IsClass && type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public) != null;
    }

    private static bool IsRecordStruct(Type type)
    {
        return type.IsValueType
            && !type.IsEnum
            && type.GetMethod("PrintMembers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null
            && type.GetInterfaces().Contains(typeof(IEquatable<>).MakeGenericType(type));
    }

    // A value of the member's type that carries the sentinel, or null when the type is not one a secret travels in.
    private static object? TrySecretValue(Type type)
    {
        if (type == typeof(string))
        {
            return Sentinel;
        }

        if (type == typeof(byte[]))
        {
            return System.Text.Encoding.UTF8.GetBytes(Sentinel);
        }

        var pairs = typeof(List<KeyValuePair<string, string>>);
        if (type.IsAssignableFrom(pairs) && type != typeof(object) && type.IsGenericType)
        {
            return new List<KeyValuePair<string, string>> { new("Authorization", Sentinel) };
        }

        if (type == typeof(Microsoft.AspNetCore.Http.QueryString))
        {
            return new Microsoft.AspNetCore.Http.QueryString("?" + Sentinel + "=1");
        }

        return null;
    }

    private static string? PrintWith(Type type, PropertyInfo property)
    {
        var instance = RuntimeHelpers.GetUninitializedObject(type);
        BackingField(property)!.SetValue(instance, TrySecretValue(property.PropertyType));

        try
        {
            return instance.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static FieldInfo? BackingField(PropertyInfo property)
    {
        for (var type = property.DeclaringType; type != null; type = type.BaseType)
        {
            var field = type.GetField($"<{property.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                return field;
            }
        }

        return null;
    }

    private static IEnumerable<Assembly> ShippedAssemblies()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<Assembly>([typeof(SecretRecordPrintTests).Assembly]);
        while (pending.Count > 0)
        {
            var assembly = pending.Dequeue();
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name == null || !reference.Name.StartsWith("Beacon.", StringComparison.Ordinal) || !seen.Add(reference.Name))
                {
                    continue;
                }

                pending.Enqueue(Assembly.Load(reference));
            }
        }

        return seen
            .Where(x => x != "Beacon.SampleProject")
            .Where(x => x != "Beacon.Testing")
            .Select(x => Assembly.Load(new AssemblyName(x)));
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
