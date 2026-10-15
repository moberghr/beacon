using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The keyed HMAC every security audit event carries: an event verifies as sealed, a change to any persisted field (every
/// column of the model but the id) or a forged hash is found, an event under a key Beacon does not hold is reported apart
/// and one moved under another held key fails, and events sealed under an earlier key keep
/// verifying (configured previous integrity keys, the derived key once an integrity key is set, keys derived from previous
/// encryption keys). A configured key that is too short or a placeholder fails startup without naming the key.
/// </summary>
[TestFixture]
public class SecurityAuditIntegrityTests
{
    private const string EncryptionKey = "the-current-encryption-key-of-this-test-0123456789";
    private const string IntegrityKey = "a-configured-integrity-key-of-this-test-0123456789";

    [Test]
    public void ASealedEvent_IsIntact_AndAChangeToAnyField_IsFound()
    {
        var integrity = new SecurityAuditIntegrity(IntegrityKey, []);
        var changes = new Action<SecurityAuditEvent>[]
        {
            x => x.Action = SecurityAuditActions.UserArchived,
            x => x.Outcome = SecurityAuditOutcomes.Failure,
            x => x.ActorUserId = 99,
            x => x.ApiKeyId = 5,
            x => x.CallerHash = "00",
            x => x.ProjectId = 2,
            x => x.ResourceId = "8",
            x => x.DetailsJson = "{\"roleIds\":[1]}",
            x => x.TraceId = "abc",
            x => x.CreatedTime = x.CreatedTime.AddTicks(10),
            x => x.EventGuid = Guid.NewGuid(),
            x => x.Hmac = new string('0', 64)
        };

        foreach (var change in changes)
        {
            var auditEvent = Sealed(integrity);
            integrity.Verify(auditEvent).Should().Be(SecurityAuditVerdict.Intact);

            change(auditEvent);

            integrity.Verify(auditEvent).Should().Be(SecurityAuditVerdict.Modified);
        }
    }

    // Driven by the EF model on both providers, so a column added to the entity fails here until the HMAC covers it.
    [Test]
    public void EveryPersistedField_ButTheIdTheHmacAndTheKeyId_IsCoveredByTheHmac()
    {
        var integrity = new SecurityAuditIntegrity(IntegrityKey, []);
        using var postgreSql = NpgsqlTestContext.Create();
        using var sqlServer = SqlServerTestContext.Create();
        string[] uncovered = [nameof(SecurityAuditEvent.Id), nameof(SecurityAuditEvent.Hmac), nameof(SecurityAuditEvent.KeyId)];
        var fields = new[] { postgreSql.Model, sqlServer.Model }
            .SelectMany(x => x.FindEntityType(typeof(SecurityAuditEvent))!.GetProperties())
            .Select(x => x.PropertyInfo)
            .OfType<PropertyInfo>()
            .Where(x => !uncovered.Contains(x.Name))
            .DistinctBy(x => x.Name)
            .ToList();

        fields.Select(x => x.Name).Should().Contain(
            [
                nameof(SecurityAuditEvent.Category), nameof(SecurityAuditEvent.ActorKind), nameof(SecurityAuditEvent.ResourceType),
                nameof(SecurityAuditEvent.EventGuid), nameof(SecurityAuditEvent.CreatedTime), nameof(SecurityAuditEvent.DetailsJson)
            ],
            "the model is what the test walks");
        foreach (var field in fields)
        {
            var auditEvent = Sealed(integrity);
            field.SetValue(auditEvent, ChangedValue(field.PropertyType, field.GetValue(auditEvent)));

            integrity.Verify(auditEvent).Should().Be(SecurityAuditVerdict.Modified, $"a change to {field.Name} must break the HMAC");
        }
    }

    [Test]
    public void AKeyIdThatIsNotHeld_IsAnUnknownKey_AndAnotherHeldKeysId_DoesNotVerify()
    {
        const string previousKey = "an-earlier-key-that-is-still-held-0123456789";
        var integrity = new SecurityAuditIntegrity(IntegrityKey, [previousKey]);
        var unknown = Sealed(integrity);
        var swapped = Sealed(integrity);

        unknown.KeyId = "k1-0000000000000000";
        swapped.KeyId = new SecurityAuditIntegrity(previousKey, []).CurrentKeyId;

        integrity.Verify(unknown).Should().Be(SecurityAuditVerdict.UnknownKey);
        integrity.Verify(swapped).Should().Be(SecurityAuditVerdict.Modified, "a row cannot be moved under another key Beacon holds");
    }

    [Test]
    public void AnEventReadBackWithoutAKind_AsSqlServerReturnsIt_StillVerifies()
    {
        var integrity = new SecurityAuditIntegrity(IntegrityKey, []);
        var auditEvent = Sealed(integrity);

        auditEvent.CreatedTime = DateTime.SpecifyKind(auditEvent.CreatedTime, DateTimeKind.Unspecified);

        integrity.Verify(auditEvent).Should().Be(SecurityAuditVerdict.Intact);
        (auditEvent.CreatedTime.Ticks % 10).Should().Be(0, "events are stored to the microsecond, the precision both providers keep");
    }

    [Test]
    public void AnEventUnderAKeyNotHeld_IsReportedAsSuch()
    {
        var auditEvent = Sealed(new SecurityAuditIntegrity("an-earlier-key-that-is-no-longer-held-0123456789", []));

        new SecurityAuditIntegrity(IntegrityKey, []).Verify(auditEvent).Should().Be(SecurityAuditVerdict.UnknownKey);
    }

    [Test]
    public void EventsUnderAPreviousKey_KeepVerifying_AndNewEventsUseTheCurrentKey()
    {
        var earlier = new SecurityAuditIntegrity("an-earlier-key-that-is-no-longer-held-0123456789", []);
        var old = Sealed(earlier);
        var current = new SecurityAuditIntegrity(IntegrityKey, ["an-earlier-key-that-is-no-longer-held-0123456789"]);

        var fresh = Sealed(current);

        current.Verify(old).Should().Be(SecurityAuditVerdict.Intact);
        fresh.KeyId.Should().Be(current.CurrentKeyId).And.NotBe(old.KeyId);
        current.Verify(fresh).Should().Be(SecurityAuditVerdict.Intact);
    }

    [Test]
    public void WithoutAnIntegrityKey_TheKeyIsDerivedFromTheCallerHashSecret_NeverItself()
    {
        var derived = SecurityAuditIntegrity.From(Configuration(), EncryptionKey, []);
        var fromCallerHashKey = SecurityAuditIntegrity.From(Configuration(callerHashKey: "a-caller-hash-key-of-this-test-0123456789abcdef"), EncryptionKey, []);

        derived.CurrentKeyId.Should().Be(new SecurityAuditIntegrity(EncryptionKey, []).CurrentKeyId);
        fromCallerHashKey.CurrentKeyId.Should().Be(new SecurityAuditIntegrity("a-caller-hash-key-of-this-test-0123456789abcdef", []).CurrentKeyId);
        derived.CurrentKeyId.Should().NotContain(EncryptionKey);
    }

    [Test]
    public void OnceAnIntegrityKeyIsSet_EventsUnderTheDerivedKey_AndUnderPreviousEncryptionKeys_StillVerify()
    {
        var beforeTheKeyWasSet = Sealed(SecurityAuditIntegrity.From(Configuration(), EncryptionKey, []));
        var beforeTheEncryptionKeyChanged = Sealed(SecurityAuditIntegrity.From(Configuration(), "the-previous-encryption-key-of-this-test-012345", []));
        var underAPreviousIntegrityKey = Sealed(new SecurityAuditIntegrity("a-previous-integrity-key-of-this-test-0123456789", []));

        var integrity = SecurityAuditIntegrity.From(
            Configuration(IntegrityKey, previousIntegrityKey: "a-previous-integrity-key-of-this-test-0123456789"),
            EncryptionKey,
            ["the-previous-encryption-key-of-this-test-012345"]);

        integrity.CurrentKeyId.Should().Be(new SecurityAuditIntegrity(IntegrityKey, []).CurrentKeyId);
        integrity.Verify(beforeTheKeyWasSet).Should().Be(SecurityAuditVerdict.Intact);
        integrity.Verify(beforeTheEncryptionKeyChanged).Should().Be(SecurityAuditVerdict.Intact);
        integrity.Verify(underAPreviousIntegrityKey).Should().Be(SecurityAuditVerdict.Intact);
    }

    [TestCase("too-short-key")]
    [TestCase("${AUDIT_INTEGRITY_KEY_FROM_SOMEWHERE_THAT_IS_LONG}")]
    public void AnUnusableIntegrityKey_FailsStartup_WithoutNamingIt(string key)
    {
        var act = () => SecurityAuditIntegrity.From(Configuration(key), EncryptionKey, []);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Beacon:Audit:IntegrityKey").And.NotContain(key);
    }

    [Test]
    public void TheSubjectHashOfACaller_IsTheMcpCallerHash_ForTheSameTenantAndSubject()
    {
        var hasher = new McpCallerSubjectHasher(EncryptionKey);

        hasher.Hash("tenant-1", "subject-1").Should().NotContain("subject-1").And.HaveLength(64);
    }

    private static SecurityAuditEvent Sealed(SecurityAuditIntegrity integrity)
    {
        return Core.SecurityAudit.SecurityAuditRecorder.ToEvent(
            new SecurityAuditEntry(SecurityAuditActions.UserCreated)
                .On(SecurityAuditResources.User, 7)
                .InProject(1)
                .With(new SecurityAuditDetails().Add("roleIds", [3])),
            new SecurityAuditActor(SecurityAuditActorKinds.User, 3),
            new DateTime(2026, 10, 14, 9, 30, 15, DateTimeKind.Utc).AddTicks(1234567),
            "0af7651916cd43dd8448eb211c80319c",
            integrity);
    }

    private static IConfiguration Configuration(string? integrityKey = null, string? previousIntegrityKey = null, string? callerHashKey = null)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:Audit:IntegrityKey"] = integrityKey,
                ["Beacon:Audit:PreviousIntegrityKeys:0"] = previousIntegrityKey,
                ["Beacon:Mcp:CallerHashKey"] = callerHashKey
            })
            .Build();
    }

    [Test]
    public void AValueDigest_IsKeyed_StableForAValue_AndTellsAnAbsentValueFromAnEmptyOne()
    {
        var integrity = SecurityAuditTestKit.Integrity;
        var other = new SecurityAuditIntegrity("another-integrity-secret-0123456789abcdef", []);

        integrity.Digest("250").Should().Be(integrity.Digest("250")).And.HaveLength(32).And.NotContain("250");
        integrity.Digest("250").Should().NotBe(integrity.Digest("251"));
        integrity.Digest(null).Should().NotBe(integrity.Digest(string.Empty));
        other.Digest("250").Should().NotBe(integrity.Digest("250"), "the digest is keyed");
    }

    // A value of the field's type that differs from the sealed one; a type without a rule here fails the test, so a new
    // kind of column is never skipped.
    private static object ChangedValue(Type type, object? current)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string))
        {
            return (string?)current + "x";
        }

        if (underlying == typeof(int))
        {
            return ((int?)current ?? 0) + 1;
        }

        if (underlying == typeof(Guid))
        {
            return Guid.NewGuid();
        }

        if (underlying == typeof(DateTime))
        {
            // One microsecond: the precision the HMAC covers.
            return ((DateTime)current!).AddTicks(10);
        }

        throw new AssertionException($"No changed value is defined for a field of type {type.Name}: add one, and cover the field in the HMAC.");
    }
}

