using System.Text.Json;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Settings;
using Beacon.Core.Services.Providers;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// What an approval of a data-source connection or of the LLM connection covers, and how it is hashed. Every hash is keyed
/// (HMAC-SHA256 under a key derived for its one purpose from the encryption key), so a stored hash of host or user names
/// cannot be tested against guesses; a hash written under a previous key still verifies. A data source's definition is its
/// connection as its driver's builder normalizes it, in any order, without its secrets: a secret-only change hashes alike,
/// any other change, an unknown setting included, does not; without a definition the whole connection data is hashed.
/// Fingerprints are short keyed digests that never show the value. The LLM connection's governed parts are its provider,
/// endpoint, region and authentication mode; a reviewer sees the endpoint as scheme and host only.
/// </summary>
[TestFixture]
public class ConnectionApprovalDefinitionsTests
{
    private const string Key = "connection-definition-test-key-one";
    private const string OtherKey = "connection-definition-test-key-two";
    private const string Canonical = "beacon:test\nhost=db.internal;user=reporting";

    private static readonly string[] NpgsqlSecrets = ["Password", "SSL Password"];

    // What carries a connection's settings prints none of them, so a log argument written later cannot leak them.
    [Test]
    public void WhatCarriesConnectionSettings_PrintsNoValue()
    {
        var definition = DataSourceConnectionDefinition.Create(
            [new KeyValuePair<string, string?>("Host", "db.internal"), new KeyValuePair<string, string?>("Username", "reporting")],
            [],
            "reporting");
        var verdict = DataSourceTransportVerdict.Pass(new DataSourceTransportTarget("db.internal", 5432, "sales")).WithDefinition(definition);
        var sqlClient = new SqlClientTransportSettings(SqlClientEncrypt.Strict, false, "warehouse.internal", "ledger") { Definition = definition };
        var llm = new LlmConnectionDefinition { Provider = AiProvider.OpenAI, Endpoint = "https://llm.internal/v1", ApiKey = "key-value", AwsAccessKeyId = "AKIDVALUE" };

        foreach (var printed in new[] { definition.ToString(), verdict.ToString(), sqlClient.ToString(), llm.ToString() })
        {
            printed.Should().NotContain("db.internal").And.NotContain("reporting").And.NotContain("sales")
                .And.NotContain("warehouse.internal").And.NotContain("ledger")
                .And.NotContain("llm.internal").And.NotContain("key-value").And.NotContain("AKIDVALUE");
        }
    }

    [Test]
    public void TheHash_IsKeyed_AndShowsNothingOfTheDefinition()
    {
        var hasher = new ConnectionDefinitionHasher(Key);
        var other = new ConnectionDefinitionHasher(OtherKey);

        var hash = hasher.Hash(ConnectionDefinitionPurpose.DataSourceDefinition, Canonical);

        hash.Should().StartWith(ConnectionDefinitionHasher.Prefix);
        hash.Length.Should().BeLessThanOrEqualTo(64, "it fits the request's definition hash column");
        hash.Should().NotContain("db.internal").And.NotContain("reporting");
        hash.Should().NotBe(other.Hash(ConnectionDefinitionPurpose.DataSourceDefinition, Canonical), "another key gives another hash");
        hasher.Matches(ConnectionDefinitionPurpose.DataSourceDefinition, hash, Canonical).Should().BeTrue();
        other.Matches(ConnectionDefinitionPurpose.DataSourceDefinition, hash, Canonical).Should().BeFalse();
        hasher.Matches(ConnectionDefinitionPurpose.DataSourceDefinition, hash, Canonical + " ").Should().BeFalse();
    }

    [Test]
    public void EachPurpose_HasItsOwnKey()
    {
        var hasher = new ConnectionDefinitionHasher(Key);

        var dataSource = hasher.Hash(ConnectionDefinitionPurpose.DataSourceDefinition, Canonical);

        hasher.Hash(ConnectionDefinitionPurpose.LlmConnection, Canonical).Should().NotBe(dataSource);
        hasher.Matches(ConnectionDefinitionPurpose.LlmConnection, dataSource, Canonical).Should().BeFalse();
        hasher.Fingerprint(ConnectionDefinitionPurpose.LlmConnection, Canonical)
            .Should().NotBe(hasher.Fingerprint(ConnectionDefinitionPurpose.DataSourceDefinition, Canonical));
    }

    [Test]
    public void AHashUnderAPreviousKey_Verifies_ButIsNotCurrent()
    {
        var before = new ConnectionDefinitionHasher(OtherKey);
        var stored = before.Hash(ConnectionDefinitionPurpose.DataSourceDefinition, Canonical);
        var storedFingerprint = before.Fingerprint(ConnectionDefinitionPurpose.LlmConnection, Canonical);

        var after = new ConnectionDefinitionHasher(Key, [OtherKey]);
        var withoutPrevious = new ConnectionDefinitionHasher(Key);

        after.HasPreviousKeys.Should().BeTrue();
        after.Matches(ConnectionDefinitionPurpose.DataSourceDefinition, stored, Canonical).Should().BeTrue();
        after.IsCurrent(ConnectionDefinitionPurpose.DataSourceDefinition, stored, Canonical).Should().BeFalse();
        after.FingerprintMatches(ConnectionDefinitionPurpose.LlmConnection, storedFingerprint, Canonical).Should().BeTrue();
        after.IsCurrentFingerprint(ConnectionDefinitionPurpose.LlmConnection, storedFingerprint, Canonical).Should().BeFalse();
        withoutPrevious.HasPreviousKeys.Should().BeFalse();
        withoutPrevious.Matches(ConnectionDefinitionPurpose.DataSourceDefinition, stored, Canonical).Should().BeFalse();
        withoutPrevious.FingerprintMatches(ConnectionDefinitionPurpose.LlmConnection, storedFingerprint, Canonical).Should().BeFalse();
        after.IsCurrent(ConnectionDefinitionPurpose.DataSourceDefinition, after.Hash(ConnectionDefinitionPurpose.DataSourceDefinition, Canonical), Canonical)
            .Should().BeTrue();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(DataSourceApprovalDefinition.Held)]
    [TestCase("not-a-hash")]
    public void AMissingHeldOrMalformedValue_MatchesNothing(string? stored)
    {
        var hasher = new ConnectionDefinitionHasher(Key);

        hasher.Matches(ConnectionDefinitionPurpose.DataSourceDefinition, stored, Canonical).Should().BeFalse();
        hasher.IsCurrent(ConnectionDefinitionPurpose.DataSourceDefinition, stored, Canonical).Should().BeFalse();
    }

    [TestCase("0123456789ab")]
    [TestCase("s3cret-password")]
    [TestCase("https://llm.example.com/openai/deployments/prod")]
    public void AFingerprint_IsTwelveHexCharacters_Keyed_AndNeverTheValue(string value)
    {
        var hasher = new ConnectionDefinitionHasher(Key);

        var fingerprint = hasher.Fingerprint(ConnectionDefinitionPurpose.LlmConnection, value);

        fingerprint.Should().HaveLength(ConnectionDefinitionHasher.FingerprintLength).And.MatchRegex("^[0-9a-f]{12}$");
        fingerprint.Should().NotBe(value).And.NotBe(value[..Math.Min(12, value.Length)]);
        fingerprint.Should().NotBe(new ConnectionDefinitionHasher(OtherKey).Fingerprint(ConnectionDefinitionPurpose.LlmConnection, value));
        hasher.IsCurrentFingerprint(ConnectionDefinitionPurpose.LlmConnection, fingerprint, value).Should().BeTrue();
    }

    [Test]
    public void TheHasher_RefusesAnEmptyKey()
    {
        var act = () => new ConnectionDefinitionHasher(" ");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void TheDefinition_IsTheSameInAnyOrder_WithoutItsSecretsAndUnsetSettings()
    {
        var first = DataSourceConnectionDefinition.Create(
            [new("Host", "db.internal"), new("Database", "sales"), new("password", "one"), new("Options", "")],
            NpgsqlSecrets,
            "reporting");
        var second = DataSourceConnectionDefinition.Create(
            [new("Database", "sales"), new("Password", "two"), new("Host", "db.internal")],
            NpgsqlSecrets,
            "reporting");

        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());
        first.Settings.Select(x => x.Key).Should().Equal("Database", "Host");
        DataSourceApprovalDefinition.Canonical(DataSourceType.Database, DatabaseEngineType.PostgreSQL, first, "Host=db.internal;Password=one")
            .Should().Be(DataSourceApprovalDefinition.Canonical(DataSourceType.Database, DatabaseEngineType.PostgreSQL, second, "Host=db.internal;Password=two"))
            .And.NotContain("one");
    }

    [Test]
    public void TheDefinition_FromABuilder_NormalizesSynonyms()
    {
        var hostAndUsername = DataSourceConnectionDefinition.FromBuilder(
            new NpgsqlConnectionStringBuilder("Host=db.internal;Username=reporting;Password=one;Database=sales"),
            new NpgsqlConnectionStringBuilder(),
            NpgsqlSecrets,
            "reporting");
        var serverAndUserId = DataSourceConnectionDefinition.FromBuilder(
            new NpgsqlConnectionStringBuilder("Database=sales;Server=db.internal;User ID=reporting;Password=two"),
            new NpgsqlConnectionStringBuilder(),
            NpgsqlSecrets,
            "reporting");
        var dataSource = DataSourceConnectionDefinition.FromBuilder(
            new SqlConnectionStringBuilder("Data Source=db.internal;User ID=reporting;Password=one"),
            new SqlConnectionStringBuilder(),
            ["Password"],
            "reporting");
        var serverAndUid = DataSourceConnectionDefinition.FromBuilder(
            new SqlConnectionStringBuilder("Server=db.internal;UID=reporting;PWD=two"),
            new SqlConnectionStringBuilder(),
            ["Password"],
            "reporting");

        hostAndUsername.Should().Be(serverAndUserId);
        hostAndUsername.Settings.Should().NotContain(x => x.Value == "one" || x.Value == "two");
        dataSource.Should().Be(serverAndUid);
        dataSource.Settings.Should().NotContain(x => x.Value == "one" || x.Value == "two");
    }

    [Test]
    public void ASecretOnlyChange_HashesAlike_AndAnyOtherChange_DoesNot()
    {
        var hasher = new ConnectionDefinitionHasher(Key);
        var stored = Hash(hasher, "Host=db.internal;Database=sales;Username=reporting;Password=one");

        Hash(hasher, "Host=db.internal;Database=sales;Username=reporting;Password=two;SSL Password=three")
            .Should().Be(stored, "a rotated secret is not a change of the definition");
        Hash(hasher, "Host=db.internal;Database=sales;Username=reporting;Password=one;Options=-c search_path=other")
            .Should().NotBe(stored);
        Hash(hasher, "Host=db.internal;Database=sales;Username=reporting;Password=one;SSL Mode=Disable")
            .Should().NotBe(stored);
        Hash(hasher, "Host=db.internal;Database=sales;Username=admin;Password=one")
            .Should().NotBe(stored);
        DataSourceApprovalDefinition.Hash(
                hasher,
                DataSourceType.Database,
                DatabaseEngineType.MySQL,
                Definition("Host=db.internal;Database=sales;Username=reporting;Password=one"),
                "unused")
            .Should().NotBe(stored, "the engine is part of the definition");
    }

    [Test]
    public void ASettingTheBuilderDoesNotKnow_IsKept()
    {
        var hasher = new ConnectionDefinitionHasher(Key);
        var without = DataSourceConnectionDefinition.Create([new("Host", "db.internal")], NpgsqlSecrets, "reporting");
        var with = DataSourceConnectionDefinition.Create([new("Host", "db.internal"), new("Custom Routing", "replica-2")], NpgsqlSecrets, "reporting");

        with.Settings.Should().Contain(new KeyValuePair<string, string>("Custom Routing", "replica-2"));
        DataSourceApprovalDefinition.Hash(hasher, DataSourceType.Database, DatabaseEngineType.PostgreSQL, with, "unused")
            .Should().NotBe(DataSourceApprovalDefinition.Hash(hasher, DataSourceType.Database, DatabaseEngineType.PostgreSQL, without, "unused"));
    }

    [Test]
    public void WithoutADefinition_TheWholeConnectionDataIsHashed_SecretsIncluded()
    {
        var hasher = new ConnectionDefinitionHasher(Key);
        const string credentials = """{"projectId":"analytics","credentialsJson":"one"}""";

        var stored = DataSourceApprovalDefinition.Hash(hasher, DataSourceType.BigQuery, null, null, credentials);

        stored.Should().NotContain("analytics").And.NotContain("one");
        DataSourceApprovalDefinition.Hash(hasher, DataSourceType.BigQuery, null, null, credentials.Replace("one", "two"))
            .Should().NotBe(stored, "without a definition a rotation is a change too");
        DataSourceApprovalDefinition.Hash(hasher, DataSourceType.BigQuery, null, null, credentials)
            .Should().Be(stored);
    }

    [Test]
    public void Allows_PassesAnUngovernedSource_RefusesAHeldOne_AndChecksTheApprovedHash()
    {
        var hasher = new ConnectionDefinitionHasher(Key, [OtherKey]);
        var canonical = DataSourceApprovalDefinition.Canonical(DataSourceType.Database, DatabaseEngineType.PostgreSQL, Definition("Host=db.internal;Password=one"), "unused");
        var approvedEarlier = new ConnectionDefinitionHasher(OtherKey).Hash(ConnectionDefinitionPurpose.DataSourceDefinition, canonical);
        var otherCanonical = DataSourceApprovalDefinition.Canonical(DataSourceType.Database, DatabaseEngineType.PostgreSQL, Definition("Host=elsewhere;Password=one"), "unused");

        DataSourceApprovalDefinition.Allows(hasher, null, canonical).Should().BeTrue("a source not governed connects as before");
        DataSourceApprovalDefinition.Allows(hasher, null, null).Should().BeTrue();
        DataSourceApprovalDefinition.Allows(hasher, DataSourceApprovalDefinition.Held, canonical).Should().BeFalse();
        DataSourceApprovalDefinition.Allows(hasher, approvedEarlier, canonical).Should().BeTrue("a previous key still verifies");
        DataSourceApprovalDefinition.Allows(hasher, approvedEarlier, otherCanonical).Should().BeFalse();
        DataSourceApprovalDefinition.Allows(hasher, approvedEarlier, null).Should().BeFalse("a connection that cannot be read is never approved");
        DataSourceApprovalDefinition.IsHeld(DataSourceApprovalDefinition.Held).Should().BeTrue();
        DataSourceApprovalDefinition.IsHeld(null).Should().BeFalse();
    }

    [Test]
    public void TheView_NamesTheChangedSettings_ShowsOnlyTheTargetAndLoginValues_AndNoSecret()
    {
        var hasher = new ConnectionDefinitionHasher(Key);
        var previous = Definition("Host=db.internal;Database=sales;Username=reporting;Password=one;Options=-c search_path=a");
        var proposed = Definition("Host=db.internal;Database=sales;Username=reporting;Password=two;Options=-c search_path=b;SSL Mode=VerifyFull");

        var view = DataSourceApprovalDefinition.View(
            hasher,
            "Sales",
            DataSourceType.Database,
            DatabaseEngineType.PostgreSQL,
            new DataSourceTransportTarget("db.internal", 5432, "sales"),
            proposed,
            "Host=db.internal;Password=two",
            previous,
            authenticatesServer: true,
            databaseReadOnlyEnforced: true,
            isTargetChange: false);
        var json = JsonSerializer.Serialize(view);

        view.ChangedSettings.Should().BeEquivalentTo("Options", "SSL Mode");
        view.Host.Should().Be("db.internal");
        view.Port.Should().Be(5432);
        view.Database.Should().Be("sales");
        view.Login.Should().Be("reporting");
        view.SecretOnlyDefinition.Should().BeFalse();
        view.Fingerprint.Should().MatchRegex("^[0-9a-f]{12}$");
        view.AuthenticatesServer.Should().BeTrue();
        json.Should().NotContain("search_path").And.NotContain("VerifyFull").And.NotContain("two").And.NotContain("one");
    }

    [Test]
    public void TheView_OfANewSource_NamesEverySetting_AndWithoutADefinition_ShowsOnlyAFingerprint()
    {
        var hasher = new ConnectionDefinitionHasher(Key);

        var created = DataSourceApprovalDefinition.View(
            hasher, "Sales", DataSourceType.Database, DatabaseEngineType.PostgreSQL, null,
            Definition("Host=db.internal;Database=sales;Password=one"), "unused", null, true, true, false);
        var whole = DataSourceApprovalDefinition.View(
            hasher, "Warehouse", DataSourceType.BigQuery, null, null,
            null, """{"projectId":"analytics","credentialsJson":"one"}""", null, true, false, false);

        created.ChangedSettings.Should().BeEquivalentTo("Database", "Host");
        whole.SecretOnlyDefinition.Should().BeTrue();
        whole.ChangedSettings.Should().BeEmpty();
        whole.Login.Should().BeNull();
        whole.Fingerprint.Should().MatchRegex("^[0-9a-f]{12}$");
        JsonSerializer.Serialize(whole).Should().NotContain("analytics").And.NotContain("credentialsJson");
    }

    [Test]
    public void TheLlmConnection_GovernsProviderEndpointRegionAndAuthMode_NotCredentialsOrModels()
    {
        var stored = Llm();

        LlmConnectionDefinition.GovernedChanges(stored, stored with { ApiKey = "rotated", AwsSecretAccessKey = "rotated" }).Should().BeEmpty();
        LlmConnectionDefinition.GovernedChanges(stored, stored with { Model = "other-model", FastModel = "other-fast" }).Should().BeEmpty();
        LlmConnectionDefinition.GovernedChanges(stored, stored with { Provider = AiProvider.Claude, Endpoint = "https://llm.example.com/other" })
            .Should().Equal(LlmConnectionDefinition.ProviderField, LlmConnectionDefinition.EndpointField);
        LlmConnectionDefinition.GovernedChanges(stored, stored with { Region = "eu-west-1", BedrockAuthMode = BedrockAuthMode.AccessKey })
            .Should().Equal(LlmConnectionDefinition.RegionField, LlmConnectionDefinition.AuthModeField);
        LlmConnectionDefinition.GovernedChanges(null, stored)
            .Should().Equal(LlmConnectionDefinition.ProviderField, LlmConnectionDefinition.EndpointField, LlmConnectionDefinition.RegionField, LlmConnectionDefinition.AuthModeField);
    }

    [Test]
    public void TheLlmConnection_ReadsTheStoredSettings_AndItsHashCoversEveryPart()
    {
        var hasher = new ConnectionDefinitionHasher(Key);
        var settings = new AppSettingsData
        {
            LlmProvider = AiProvider.AzureOpenAI,
            LlmEndpoint = "https://llm.example.com/openai/deployments/prod",
            LlmRegion = "westeurope",
            LlmApiKey = "key-one",
            LlmModel = "model-a"
        };

        var stored = LlmConnectionDefinition.Of(settings);

        stored.Should().Be(Llm());
        stored.Hash(hasher).Should().StartWith(ConnectionDefinitionHasher.Prefix).And.NotContain("llm.example.com");
        stored.Hash(hasher).Should().NotBe((stored with { ApiKey = "key-two" }).Hash(hasher), "the proposal binds every credential");
        stored.Hash(hasher).Should().NotBe((stored with { Model = "model-b" }).Hash(hasher));
        stored.Fingerprint(hasher).Should().MatchRegex("^[0-9a-f]{12}$");
    }

    [Test]
    public void TheLlmView_ShowsTheEndpointAsSchemeAndHost_AndFingerprintsTheRest()
    {
        var hasher = new ConnectionDefinitionHasher(Key);
        var stored = Llm() with { Endpoint = "https://llm.example.com:8443/openai/deployments/prod?api-version=1" };

        var view = stored.View(hasher, basedOn: "0123456789ab");
        var json = JsonSerializer.Serialize(view);

        view.Endpoint.Should().Be("https://llm.example.com:8443");
        view.EndpointFingerprint.Should().MatchRegex("^[0-9a-f]{12}$");
        view.EndpointFingerprint.Should().NotBe(Llm().View(hasher, null).EndpointFingerprint, "a change of the path shows in the fingerprint");
        view.Provider.Should().Be(AiProvider.AzureOpenAI);
        view.Region.Should().Be("westeurope");
        view.Model.Should().Be("model-a");
        view.BasedOn.Should().Be("0123456789ab");
        view.Credentials.Should().ContainSingle()
            .Which.Name.Should().Be(LlmConnectionDefinition.ApiKeyField);
        view.Credentials[0].Fingerprint.Should().MatchRegex("^[0-9a-f]{12}$");
        json.Should().NotContain("deployments").And.NotContain("api-version").And.NotContain("key-one");
        LlmConnectionDefinition.EndpointShown("not a url").Should().NotContain("not a url");
        LlmConnectionDefinition.EndpointShown(null).Should().BeNull();
        LlmConnectionDefinition.HostConfigurationView().FromHostConfiguration.Should().BeTrue();
        LlmConnectionDefinition.HostConfigurationView().Endpoint.Should().BeNull();
    }

    private static LlmConnectionDefinition Llm()
    {
        return new LlmConnectionDefinition
        {
            Provider = AiProvider.AzureOpenAI,
            Endpoint = "https://llm.example.com/openai/deployments/prod",
            Region = "westeurope",
            BedrockAuthMode = BedrockAuthMode.IamRole,
            Model = "model-a",
            ApiKey = "key-one"
        };
    }

    private static DataSourceConnectionDefinition Definition(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        return DataSourceConnectionDefinition.FromBuilder(builder, new NpgsqlConnectionStringBuilder(), NpgsqlSecrets, builder.Username);
    }

    private static string Hash(ConnectionDefinitionHasher hasher, string connectionString)
    {
        return DataSourceApprovalDefinition.Hash(hasher, DataSourceType.Database, DatabaseEngineType.PostgreSQL, Definition(connectionString), connectionString);
    }
}
