using System.Reflection;
using Beacon.Connector.AzureSynapse;
using Beacon.Connector.SqlServer;
using Beacon.Core.ChangeApprovals;
using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services.Providers;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.DataSourceTransport;

/// <summary>
/// Which connections authenticate the server over TLS, per engine, as each connector's inspector reads them with its
/// driver's own parser (option synonyms and case included), and what a refusal says: the option to change, never a value
/// read from the connection string.
/// </summary>
[TestFixture]
public class DataSourceTransportInspectorTests
{
    private const string Host = "leaky-host-7f3a.internal";

    private const string Database = "secret_db_91";

    private const string User = "reader_c4d2";

    private static readonly string Password = TestCredentials.ProviderSecretKey;

    public static IEnumerable<TestCaseData> Connections()
    {
        // PostgreSQL: SSL Mode=VerifyFull only.
        yield return Case(DatabaseEngineType.PostgreSQL, $"Host={Host};Database={Database};Username={User};Password={Password};SSL Mode=VerifyFull", null);
        yield return Case(DatabaseEngineType.PostgreSQL, $"Host={Host};Password={Password};SslMode=verifyfull", null);
        yield return Case(DatabaseEngineType.PostgreSQL, $"host={Host};password={Password};SSLMODE=VERIFYFULL", null);
        yield return Case(DatabaseEngineType.PostgreSQL, $"Host={Host};Password={Password};SSL Mode=VerifyFull;Root Certificate=/etc/ssl/beacon-ca.pem", null);
        yield return Case(DatabaseEngineType.PostgreSQL, $"Host={Host};Database={Database};Password={Password}", "SSL Mode");
        yield return Case(DatabaseEngineType.PostgreSQL, $"Host={Host};Password={Password};SSL Mode=Require", "SSL Mode");
        yield return Case(DatabaseEngineType.PostgreSQL, $"Host={Host};Password={Password};sslmode=verifyca", "SSL Mode");
        yield return Case(DatabaseEngineType.PostgreSQL, $"Host={Host};Password={Password};SSL Mode=Disable", "SSL Mode");
        yield return Case(DatabaseEngineType.PostgreSQL, $"Host={Host};Password={Password};SSL Mode=Prefer", "SSL Mode");
        // Npgsql still accepts the obsolete Trust Server Certificate option; the check reads SSL Mode, which stays VerifyFull.
        yield return Case(DatabaseEngineType.PostgreSQL, $"Host={Host};Password={Password};SSL Mode=VerifyFull;Trust Server Certificate=true", null);
        yield return Case(DatabaseEngineType.PostgreSQL, string.Empty, "connection string");
        yield return Case(DatabaseEngineType.PostgreSQL, "   ", "connection string");

        // SQL Server and Synapse: Encrypt Mandatory or Strict, TrustServerCertificate false (SqlClient 6 defaults pass).
        foreach (var engine in new[] { DatabaseEngineType.MSSQL, DatabaseEngineType.AzureSynapse })
        {
            yield return Case(engine, $"Server=tcp:{Host},1433;Database={Database};User Id={User};Password={Password}", null);
            yield return Case(engine, $"Server={Host};Password={Password};Encrypt=True", null);
            yield return Case(engine, $"Server={Host};Password={Password};Encrypt=Mandatory;TrustServerCertificate=False", null);
            yield return Case(engine, $"Server={Host};Password={Password};encrypt=strict;HostNameInCertificate={Host}", null);
            yield return Case(engine, $"Server={Host};Password={Password};Encrypt=False", "Encrypt");
            yield return Case(engine, $"Server={Host};Password={Password};Encrypt=optional", "Encrypt");
            yield return Case(engine, $"Server={Host};Password={Password};Encrypt=no", "Encrypt");
            yield return Case(engine, $"Server={Host};Password={Password};TrustServerCertificate=True", "TrustServerCertificate");
            yield return Case(engine, $"Server={Host};Password={Password};Trust Server Certificate=yes", "TrustServerCertificate");
            yield return Case(engine, $"Server={Host};Password={Password};Encrypt=True;trustservercertificate=TRUE", "TrustServerCertificate");
        }

        // MySQL: SslMode=VerifyFull only.
        yield return Case(DatabaseEngineType.MySQL, $"Server={Host};Database={Database};Uid={User};Pwd={Password};SslMode=VerifyFull", null);
        yield return Case(DatabaseEngineType.MySQL, $"Server={Host};Pwd={Password};ssl mode=verifyfull;SslCa=/etc/ssl/beacon-ca.pem", null);
        yield return Case(DatabaseEngineType.MySQL, $"Server={Host};Pwd={Password};Ssl-Mode=VERIFYFULL", null);
        yield return Case(DatabaseEngineType.MySQL, $"Server={Host};Database={Database};Pwd={Password}", "SslMode");
        yield return Case(DatabaseEngineType.MySQL, $"Server={Host};Pwd={Password};SslMode=Required", "SslMode");
        yield return Case(DatabaseEngineType.MySQL, $"Server={Host};Pwd={Password};SslMode=VerifyCA", "SslMode");
        yield return Case(DatabaseEngineType.MySQL, $"Server={Host};Pwd={Password};sslmode=disabled", "SslMode");

        // Snowflake: no insecureMode, no scheme other than https.
        yield return Case(DatabaseEngineType.Snowflake, $"account={Host};user={User};password={Password};db={Database}", null);
        yield return Case(DatabaseEngineType.Snowflake, $"account={Host};password={Password};insecureMode=false", null);
        yield return Case(DatabaseEngineType.Snowflake, $"account={Host};password={Password};SCHEME=HTTPS", null);
        yield return Case(DatabaseEngineType.Snowflake, $"account={Host};password={Password};insecureMode=true", "insecureMode");
        yield return Case(DatabaseEngineType.Snowflake, $"account={Host};password={Password};INSECUREMODE=True", "insecureMode");
        yield return Case(DatabaseEngineType.Snowflake, $"account={Host};password={Password};insecuremode={Database}", "insecureMode");
        yield return Case(DatabaseEngineType.Snowflake, $"account={Host};password={Password};scheme=http", "scheme");
        yield return Case(DatabaseEngineType.Snowflake, $"account={Host};password={Password};certRevocationCheckMode=ENABLED", null);
        yield return Case(DatabaseEngineType.Snowflake, $"account={Host};password={Password};CERTREVOCATIONCHECKMODE=enabled", null);
        yield return Case(DatabaseEngineType.Snowflake, $"account={Host};password={Password};certRevocationCheckMode=ADVISORY", "certRevocationCheckMode");
        yield return Case(DatabaseEngineType.Snowflake, $"account={Host};password={Password};certrevocationcheckmode=disabled", "certRevocationCheckMode");
        yield return Case(DatabaseEngineType.Snowflake, $"account={Host};password={Password};certRevocationCheckMode={Database}", "certRevocationCheckMode");

        // A database engine without an inspector is never verified.
        yield return Case(DatabaseEngineType.SQLite, $"Data Source={Database}.db;Password={Password}", "data source type");
    }

    public static IEnumerable<TestCaseData> Documents()
    {
        // API: BaseUrl and OpenApiSpecUrl both absolute https URLs.
        yield return Document(DataSourceType.Api, Api($"https://{Host}/v1", $"https://{Host}/openapi.json"), null);
        yield return Document(DataSourceType.Api, $"{{\"BASEURL\":\"HTTPS://{Host}\",\"openapispecurl\":\"https://{Host}/spec\",\"auth\":{{\"token\":\"{Password}\"}}}}", null);
        yield return Document(DataSourceType.Api, Api($"http://{Host}/v1", $"https://{Host}/openapi.json"), "BaseUrl");
        yield return Document(DataSourceType.Api, Api($"https://{Host}/v1", $"http://{Host}/openapi.json"), "OpenApiSpecUrl");
        yield return Document(DataSourceType.Api, Api($"/{Database}", $"https://{Host}/openapi.json"), "BaseUrl");
        yield return Document(DataSourceType.Api, Api($"ftp://{Host}/v1", $"https://{Host}/openapi.json"), "BaseUrl");
        // OpenApiSpecUrl is a required member of the configuration: without it the document cannot be read at all.
        yield return Document(DataSourceType.Api, $"{{\"baseUrl\":\"https://{Host}/v1\",\"auth\":{{\"token\":\"{Password}\"}}}}", "connection string");
        yield return Document(DataSourceType.Api, "null", "connection string");
        yield return Document(DataSourceType.Api, "{", "connection string");
        yield return Document(DataSourceType.Api, " ", "connection string");

        // TLS-only SDKs pass whatever their configuration says.
        yield return Document(DataSourceType.Databricks, $"{{\"host\":\"{Host}\",\"token\":\"{Password}\"}}", null);
        yield return Document(DataSourceType.BigQuery, $"{{\"projectId\":\"{Database}\",\"credentialsJson\":\"{Password}\"}}", null);
        yield return Document(DataSourceType.CloudWatch, $"{{\"region\":\"eu-west-1\",\"secretAccessKey\":\"{Password}\"}}", null);
        yield return Document(DataSourceType.Databricks, string.Empty, "connection string");
        yield return Document(DataSourceType.Databricks, "\t ", "connection string");

        // A type without a connector is never verified.
        yield return Document(DataSourceType.Prometheus, $"{{\"url\":\"https://{Host}\"}}", "data source type");
    }

    [TestCaseSource(nameof(Connections))]
    [TestCaseSource(nameof(Documents))]
    public void TheVerdict_FollowsTheEnginesTransportOptions(DataSourceType type, DatabaseEngineType? engine, string connectionData, string? failingOption)
    {
        var verdict = TestTransport.Policy().Inspect(type, engine, connectionData);

        verdict.Verified.Should().Be(failingOption == null);
        verdict.Option.Should().Be(failingOption);
    }

    [TestCaseSource(nameof(Connections))]
    [TestCaseSource(nameof(Documents))]
    public void ARefusal_NamesTheOption_AndNeverAValueFromTheConnectionString(DataSourceType type, DatabaseEngineType? engine, string connectionData, string? failingOption)
    {
        var act = () => TestTransport.Policy().EnsureNewConnectionAllowed(type, engine, connectionData);

        if (failingOption == null)
        {
            act.Should().NotThrow();
            return;
        }

        var message = act.Should().Throw<DataSourceTransportException>().Which.Message;
        message.Should().Contain(failingOption == "data source type" ? "No transport check" : failingOption);
        message.Should().NotContainAny(Host, Database, User, Password, "/etc/ssl");
    }

    [TestCase(DatabaseEngineType.PostgreSQL, "Host=" + Host + ";NoSuchOption={0};SSL Mode=VerifyFull")]
    [TestCase(DatabaseEngineType.MSSQL, "Server=" + Host + ";NoSuchOption={0}")]
    [TestCase(DatabaseEngineType.AzureSynapse, "Server=" + Host + ";NoSuchOption={0}")]
    [TestCase(DatabaseEngineType.MSSQL, "Server=" + Host + ";Encrypt={0}")]
    [TestCase(DatabaseEngineType.AzureSynapse, "Server=" + Host + ";Encrypt={0}")]
    [TestCase(DatabaseEngineType.MySQL, "Server=" + Host + ";SslMode={0}")]
    [TestCase(DatabaseEngineType.PostgreSQL, "{0}")]
    public void AConnectionStringTheDriverCannotRead_IsRefused_WithoutTheParsersMessage(DatabaseEngineType engine, string template)
    {
        var connectionString = string.Format(template, Password);

        var verdict = TestTransport.Policy().Inspect(DataSourceType.Database, engine, connectionString);
        var act = () => TestTransport.Policy().EnsureNewConnectionAllowed(DataSourceType.Database, engine, connectionString);

        verdict.Verified.Should().BeFalse();
        verdict.Option.Should().Be("connection string");
        act.Should().Throw<DataSourceTransportException>().Which.Message.Should().NotContainAny(Host, Password, "NoSuchOption");
    }

    // The SqlClient verdict is decided in Core on the settings the connector reads; a mode Core does not know fails.
    [TestCase(SqlClientEncrypt.Mandatory, false, null)]
    [TestCase(SqlClientEncrypt.Strict, false, null)]
    [TestCase(SqlClientEncrypt.Optional, false, "Encrypt")]
    [TestCase(SqlClientEncrypt.Optional, true, "Encrypt")]
    [TestCase(SqlClientEncrypt.Strict, true, "TrustServerCertificate")]
    [TestCase((SqlClientEncrypt)7, false, "Encrypt")]
    public void TheSqlClientVerdict_IsDecidedOnTheSettingsTheConnectorReads(SqlClientEncrypt encrypt, bool trustServerCertificate, string? failingOption)
    {
        var verdict = new FixedSqlClientInspector(new SqlClientTransportSettings(encrypt, trustServerCertificate, $"tcp:{Host},1433", Database))
            .Inspect("read by the stand-in");

        verdict.Verified.Should().Be(failingOption == null);
        verdict.Option.Should().Be(failingOption);
        verdict.Target.Should().Be(new DataSourceTransportTarget(Host, 1433, Database));
    }

    // The connectors' own reader (SqlClient's builder) feeds the verdict: pin what it maps, through each connector's inspector.
    [TestCase(typeof(SqlServerTransportInspector), "Encrypt=Strict", SqlClientEncrypt.Strict, false)]
    [TestCase(typeof(SqlServerTransportInspector), "Encrypt=Mandatory", SqlClientEncrypt.Mandatory, false)]
    [TestCase(typeof(SqlServerTransportInspector), "Encrypt=True", SqlClientEncrypt.Mandatory, false)]
    [TestCase(typeof(SqlServerTransportInspector), "", SqlClientEncrypt.Mandatory, false)]
    [TestCase(typeof(SqlServerTransportInspector), "Encrypt=Optional", SqlClientEncrypt.Optional, false)]
    [TestCase(typeof(SqlServerTransportInspector), "Encrypt=False;TrustServerCertificate=True", SqlClientEncrypt.Optional, true)]
    [TestCase(typeof(SqlServerTransportInspector), "Encrypt=Strict;TrustServerCertificate=True", SqlClientEncrypt.Strict, true)]
    [TestCase(typeof(AzureSynapseTransportInspector), "Encrypt=Strict", SqlClientEncrypt.Strict, false)]
    [TestCase(typeof(AzureSynapseTransportInspector), "Encrypt=Mandatory", SqlClientEncrypt.Mandatory, false)]
    [TestCase(typeof(AzureSynapseTransportInspector), "Encrypt=True", SqlClientEncrypt.Mandatory, false)]
    [TestCase(typeof(AzureSynapseTransportInspector), "", SqlClientEncrypt.Mandatory, false)]
    [TestCase(typeof(AzureSynapseTransportInspector), "Encrypt=Optional", SqlClientEncrypt.Optional, false)]
    [TestCase(typeof(AzureSynapseTransportInspector), "Encrypt=False;TrustServerCertificate=True", SqlClientEncrypt.Optional, true)]
    public void TheConnectorsReader_MapsSqlClientsOptions_ToTheSettingsTheVerdictUses(Type inspectorType, string options, SqlClientEncrypt encrypt, bool trustServerCertificate)
    {
        var inspector = Activator.CreateInstance(inspectorType)!;
        var read = inspectorType.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var settings = (SqlClientTransportSettings)read.Invoke(inspector, [$"Server=tcp:{Host},1433;Database={Database};{options}"])!;

        settings.Encrypt.Should().Be(encrypt);
        settings.TrustServerCertificate.Should().Be(trustServerCertificate);
        settings.DataSource.Should().Be($"tcp:{Host},1433");
        settings.InitialCatalog.Should().Be(Database);
    }

    [Test]
    public void TheVerdict_NamesTheTarget_ForTheCallerThatComparesIt()
    {
        var policy = TestTransport.Policy();

        policy.Inspect(DataSourceType.Database, DatabaseEngineType.PostgreSQL, $"Host={Host};Port=6432;Database={Database};SSL Mode=VerifyFull").Target
            .Should().Be(new DataSourceTransportTarget(Host, 6432, Database));
        policy.Inspect(DataSourceType.Database, DatabaseEngineType.MSSQL, $"Server=tcp:{Host},1433;Initial Catalog={Database}").Target
            .Should().Be(new DataSourceTransportTarget(Host, 1433, Database));
        policy.Inspect(DataSourceType.Database, DatabaseEngineType.AzureSynapse, $"Server={Host};Database={Database};Encrypt=False").Target
            .Should().Be(new DataSourceTransportTarget(Host, null, Database), "a failing verdict names the target too");
        policy.Inspect(DataSourceType.Database, DatabaseEngineType.MySQL, $"Server={Host};Port=3307;Database={Database};SslMode=VerifyFull").Target
            .Should().Be(new DataSourceTransportTarget(Host, 3307, Database));
        policy.Inspect(DataSourceType.Database, DatabaseEngineType.Snowflake, $"account={Host};port=8443;db={Database}").Target
            .Should().Be(new DataSourceTransportTarget(Host, 8443, Database));
        policy.Inspect(DataSourceType.Api, null, Api($"https://{Host}:8443/v1", $"https://{Host}/openapi.json")).Target
            .Should().Be(new DataSourceTransportTarget(Host, 8443, null));
        policy.Inspect(DataSourceType.Databricks, null, $"{{\"host\":\"{Host}\"}}").Target
            .Should().BeNull("a TLS-only connector's check does not read its configuration");
    }

    [Test]
    public void AnApiSource_IsCheckedAsAnApi_WhateverEngineItStillCarries()
    {
        var verdict = TestTransport.Policy().Inspect(DataSourceType.Api, DatabaseEngineType.PostgreSQL, Api($"https://{Host}/v1", $"https://{Host}/openapi.json"));

        verdict.Verified.Should().BeTrue("the engine of a source that is not a database is ignored");
    }

    public static IEnumerable<TestCaseData> SameDefinitions()
    {
        // The driver's synonyms name one setting.
        yield return Pair(DatabaseEngineType.PostgreSQL, "synonyms",
            $"Host={Host};Username={User};Password={Password};Database={Database};SSL Mode=VerifyFull",
            $"server={Host};User ID={User};Password={Password};DB={Database};sslmode=verifyfull");
        yield return Pair(DatabaseEngineType.MSSQL, "synonyms",
            $"Server={Host};User ID={User};Password={Password};Database={Database}",
            $"Data Source={Host};UID={User};PWD={Password};Initial Catalog={Database}");
        yield return Pair(DatabaseEngineType.AzureSynapse, "synonyms",
            $"Address={Host};User ID={User};Password={Password};Database={Database}",
            $"Data Source={Host};UID={User};PWD={Password};Initial Catalog={Database}");
        yield return Pair(DatabaseEngineType.MySQL, "synonyms",
            $"Server={Host};Uid={User};Pwd={Password};Database={Database};SslMode=VerifyFull",
            $"Host={Host};User ID={User};Password={Password};Database={Database};ssl mode=verifyfull");
        yield return Pair(DatabaseEngineType.Snowflake, "synonyms",
            $"ACCOUNT={Host};USER={User};PASSWORD={Password};DB={Database}",
            $"account={Host};user={User};password={Password};db={Database}");

        // A change of secrets alone is no change of the definition.
        yield return Pair(DatabaseEngineType.PostgreSQL, "secrets only",
            $"Host={Host};Username={User};Password={Password};SSL Mode=VerifyFull",
            $"Host={Host};Username={User};Password=rotated-1;SSL Password=rotated-2;SSL Mode=VerifyFull");
        yield return Pair(DatabaseEngineType.MSSQL, "secrets only",
            $"Server={Host};User ID={User};Password={Password}",
            $"Server={Host};User ID={User};Password=rotated-1");
        yield return Pair(DatabaseEngineType.AzureSynapse, "secrets only",
            $"Server={Host};User ID={User};Password={Password}",
            $"Server={Host};User ID={User};Pwd=rotated-1");
        yield return Pair(DatabaseEngineType.MySQL, "secrets only",
            $"Server={Host};Uid={User};Pwd={Password};SslMode=VerifyFull",
            $"Server={Host};Uid={User};Pwd=rotated-1;Password2=rotated-2;Password3=rotated-3;CertificatePassword=rotated-4;OpenIdIdentityToken=rotated-5;SslMode=VerifyFull");
        yield return Pair(DatabaseEngineType.Snowflake, "secrets only",
            $"account={Host};user={User};password={Password}",
            $"account={Host};user={User};password=rotated-1;passcode=rotated-2;private_key=rotated-3;private_key_pwd=rotated-4;token=rotated-5;proxyPassword=rotated-6;oauthClientSecret=rotated-7");

        // A default typed out is the default left out.
        yield return Pair(DatabaseEngineType.PostgreSQL, "defaults",
            $"Host={Host};Username={User};SSL Mode=VerifyFull",
            $"Host={Host};Username={User};SSL Mode=VerifyFull;Port=5432;Pooling=true;Command Timeout=30");
        yield return Pair(DatabaseEngineType.PostgreSQL, "default SSL mode",
            $"Host={Host};Username={User}",
            $"Host={Host};Username={User};SSL Mode=Prefer");
        yield return Pair(DatabaseEngineType.MSSQL, "defaults",
            $"Server={Host};User ID={User}",
            $"Server={Host};User ID={User};Encrypt=True;TrustServerCertificate=False;Application Intent=ReadWrite;Pooling=yes");
        yield return Pair(DatabaseEngineType.AzureSynapse, "defaults",
            $"Server={Host};User ID={User}",
            $"Server={Host};User ID={User};Encrypt=Mandatory;Trust Server Certificate=false;MultiSubnetFailover=False");
        yield return Pair(DatabaseEngineType.MySQL, "defaults",
            $"Server={Host};Uid={User};SslMode=VerifyFull",
            $"Server={Host};Uid={User};SslMode=VerifyFull;Port=3306;Pooling=true");
        yield return Pair(DatabaseEngineType.Snowflake, "defaults",
            $"account={Host};user={User}",
            $"account={Host};user={User};scheme=HTTPS;port=443;insecureMode=False;authenticator=snowflake");
    }

    public static IEnumerable<TestCaseData> OtherDefinitions()
    {
        const string PostgreSql = $"Host={Host};Username={User};Password=one;Database={Database};SSL Mode=VerifyFull";
        yield return Pair(DatabaseEngineType.PostgreSQL, "options", PostgreSql, PostgreSql + ";Options=-c search_path=other");
        yield return Pair(DatabaseEngineType.PostgreSQL, "SSL mode", PostgreSql, PostgreSql.Replace("VerifyFull", "Require", StringComparison.Ordinal));
        yield return Pair(DatabaseEngineType.PostgreSQL, "root certificate", PostgreSql, PostgreSql + ";Root Certificate=/etc/ssl/other-ca.pem");
        yield return Pair(DatabaseEngineType.PostgreSQL, "user", PostgreSql, PostgreSql.Replace(User, "admin_9e1b", StringComparison.Ordinal));
        yield return Pair(DatabaseEngineType.PostgreSQL, "port", PostgreSql, PostgreSql + ";Port=6432");

        foreach (var engine in new[] { DatabaseEngineType.MSSQL, DatabaseEngineType.AzureSynapse })
        {
            const string SqlClient = $"Server={Host};User ID={User};Password=one;Database={Database}";
            yield return Pair(engine, "failover partner", SqlClient, SqlClient + ";Failover Partner=standby-2c1d.internal");
            yield return Pair(engine, "application intent", SqlClient, SqlClient + ";ApplicationIntent=ReadOnly");
            yield return Pair(engine, "encrypt", SqlClient, SqlClient + ";Encrypt=Strict");
            yield return Pair(engine, "authentication", SqlClient, SqlClient + ";Authentication=Active Directory Password");
            yield return Pair(engine, "database", SqlClient, SqlClient.Replace(Database, "other_db", StringComparison.Ordinal));
        }

        const string MySql = $"Server={Host};Uid={User};Pwd=one;Database={Database};SslMode=VerifyFull";
        yield return Pair(DatabaseEngineType.MySQL, "SSL mode", MySql, MySql.Replace("VerifyFull", "Required", StringComparison.Ordinal));
        yield return Pair(DatabaseEngineType.MySQL, "CA file", MySql, MySql + ";SslCa=/etc/ssl/other-ca.pem");
        yield return Pair(DatabaseEngineType.MySQL, "port", MySql, MySql + ";Port=3307");

        const string Snowflake = $"account={Host};user={User};password=one;db={Database}";
        yield return Pair(DatabaseEngineType.Snowflake, "role", Snowflake, Snowflake + ";role=ACCOUNTADMIN");
        yield return Pair(DatabaseEngineType.Snowflake, "warehouse", Snowflake, Snowflake + ";warehouse=LARGE_WH");
        yield return Pair(DatabaseEngineType.Snowflake, "authenticator", Snowflake, Snowflake + ";authenticator=externalbrowser");
        yield return Pair(DatabaseEngineType.Snowflake, "unknown key", Snowflake, Snowflake + ";customRouting=replica-2");
    }

    [TestCaseSource(nameof(SameDefinitions))]
    public void TheDefinition_IsTheSame_ForTheSameConnectionWrittenOtherwise(DatabaseEngineType engine, string first, string second)
    {
        var policy = TestTransport.Policy();

        var one = policy.Inspect(DataSourceType.Database, engine, first).Definition;
        var other = policy.Inspect(DataSourceType.Database, engine, second).Definition;

        one.Should().NotBeNull();
        other.Should().Be(one);
        DataSourceApprovalDefinition.Canonical(DataSourceType.Database, engine, other, second)
            .Should().Be(DataSourceApprovalDefinition.Canonical(DataSourceType.Database, engine, one, first));
    }

    [TestCaseSource(nameof(OtherDefinitions))]
    public void TheDefinition_Changes_WithAnySettingButASecret(DatabaseEngineType engine, string first, string second)
    {
        var policy = TestTransport.Policy();

        var one = policy.Inspect(DataSourceType.Database, engine, first).Definition;
        var other = policy.Inspect(DataSourceType.Database, engine, second).Definition;

        one.Should().NotBeNull();
        other.Should().NotBeNull();
        other.Should().NotBe(one);
    }

    [TestCase(DatabaseEngineType.PostgreSQL, $"Host={Host};Username={User};Password={{0}};SSL Password={{0}};Database={Database};SSL Mode=Require")]
    [TestCase(DatabaseEngineType.MSSQL, $"Server=tcp:{Host},1433;User ID={User};Password={{0}};Database={Database};Encrypt=False")]
    [TestCase(DatabaseEngineType.AzureSynapse, $"Server={Host};UID={User};PWD={{0}};Database={Database}")]
    [TestCase(DatabaseEngineType.MySQL, $"Server={Host};Uid={User};Pwd={{0}};Password2={{0}};CertificatePassword={{0}};Database={Database}")]
    [TestCase(DatabaseEngineType.Snowflake, $"account={Host};user={User};password={{0}};passcode={{0}};private_key={{0}};private_key_pwd={{0}};token={{0}};proxypassword={{0}};oauthClientSecret={{0}};db={Database}")]
    public void TheDefinition_NamesTheLogin_AndHoldsNoSecret_WhateverTheVerdict(DatabaseEngineType engine, string template)
    {
        var connectionString = string.Format(template, Password);

        var definition = TestTransport.Policy().Inspect(DataSourceType.Database, engine, connectionString).Definition;

        definition.Should().NotBeNull();
        definition!.Login.Should().Be(User);
        definition.Settings.Should().Contain(x => x.Value.Contains(Host, StringComparison.Ordinal));
        definition.Settings.Should().Contain(x => x.Value == Database);
        definition.Settings.Should().NotContain(x => x.Value.Contains(Password, StringComparison.Ordinal));
        definition.Settings.Select(x => x.Key).Should().BeInAscendingOrder(StringComparer.Ordinal);
        DataSourceApprovalDefinition.Canonical(DataSourceType.Database, engine, definition, connectionString).Should().NotContain(Password);
    }

    // Each secret the engine's driver reads is left out of the definition: a change of it alone hashes alike, so it is a
    // rotation, never a change that waits for approval.
    [TestCase(DatabaseEngineType.PostgreSQL, $"Host={Host};Username={User};Password={{0}};SSL Password={{0}};Database={Database};SSL Mode=VerifyFull")]
    [TestCase(DatabaseEngineType.MSSQL, $"Server=tcp:{Host},1433;User ID={User};Password={{0}};Database={Database};Encrypt=Strict")]
    [TestCase(DatabaseEngineType.AzureSynapse, $"Server={Host};UID={User};PWD={{0}};Database={Database};Encrypt=Strict")]
    [TestCase(DatabaseEngineType.MySQL, $"Server={Host};Uid={User};Pwd={{0}};Password2={{0}};Password3={{0}};CertificatePassword={{0}};Database={Database};SslMode=VerifyFull")]
    [TestCase(DatabaseEngineType.Snowflake, $"account={Host};user={User};password={{0}};passcode={{0}};private_key={{0}};private_key_pwd={{0}};token={{0}};proxypassword={{0}};oauthClientSecret={{0}};db={Database}")]
    public void AChangeOfTheSecretsAlone_KeepsTheDefinition(DatabaseEngineType engine, string template)
    {
        var policy = TestTransport.Policy();
        var before = string.Format(template, "first-secret");
        var after = string.Format(template, "second-secret");

        DataSourceApprovalDefinition.Canonical(policy, DataSourceType.Database, engine, after)
            .Should().Be(DataSourceApprovalDefinition.Canonical(policy, DataSourceType.Database, engine, before))
            .And.NotContain("first-secret").And.NotContain("second-secret");
    }

    [TestCase(DatabaseEngineType.PostgreSQL, $"Host={Host};SSL Mode=VerifyFull", "Integrated Security")]
    [TestCase(DatabaseEngineType.MSSQL, $"Server={Host};Authentication=Active Directory Managed Identity", "ActiveDirectoryManagedIdentity")]
    [TestCase(DatabaseEngineType.AzureSynapse, $"Server={Host};Integrated Security=SSPI", "Integrated Security")]
    [TestCase(DatabaseEngineType.MySQL, $"Server={Host};DefaultAuthenticationPlugin=authentication_kerberos_client", "authentication_kerberos_client")]
    [TestCase(DatabaseEngineType.Snowflake, $"account={Host};authenticator=OAUTH;token=t", "OAUTH")]
    public void WithoutAUserName_TheLoginIsTheAuthenticationMode(DatabaseEngineType engine, string connectionString, string login)
    {
        var definition = TestTransport.Policy().Inspect(DataSourceType.Database, engine, connectionString).Definition;

        definition.Should().NotBeNull();
        definition!.Login.Should().Be(login);
    }

    [Test]
    public void ASnowflakeSettingTheDriverDoesNotKnow_IsKept()
    {
        var definition = TestTransport.Policy()
            .Inspect(DataSourceType.Database, DatabaseEngineType.Snowflake, $"account={Host};user={User};customRouting=replica-2")
            .Definition;

        definition!.Settings.Should().Contain(new KeyValuePair<string, string>("customrouting", "replica-2"));
    }

    [Test]
    public void TheSqlClientVerdict_CarriesTheDefinitionTheConnectorReads()
    {
        var definition = DataSourceConnectionDefinition.Create([new("Data Source", Host)], ["Password"], User);
        var settings = new SqlClientTransportSettings(SqlClientEncrypt.Optional, false, Host, Database) { Definition = definition };

        var verdict = new FixedSqlClientInspector(settings).Inspect("read by the stand-in");

        verdict.Verified.Should().BeFalse();
        verdict.Definition.Should().BeSameAs(definition);
    }

    [Test]
    public void AConnectionNobodyCanRead_HasNoDefinition()
    {
        var policy = TestTransport.Policy();

        policy.Inspect(DataSourceType.Database, DatabaseEngineType.PostgreSQL, $"Host={Host};NoSuchOption=1").Definition.Should().BeNull();
        policy.Inspect(DataSourceType.Database, DatabaseEngineType.SQLite, $"Data Source={Database}.db").Definition.Should().BeNull();
        policy.Inspect(DataSourceType.Databricks, null, $"{{\"host\":\"{Host}\"}}").Definition.Should().BeNull("a TLS-only connector gives no definition");
    }

    private const string ApiBase = $"\"baseUrl\":\"https://{Host}/v1\",\"openApiSpecUrl\":\"https://{Host}/spec\"";

    public static IEnumerable<TestCaseData> SameApiDefinitions()
    {
        yield return ApiPair("property case",
            $"{{{ApiBase},\"auth\":{{\"type\":\"Basic\",\"username\":\"{User}\",\"password\":\"one\"}}}}",
            $"{{\"BaseUrl\":\"https://{Host}/v1\",\"OPENAPISPECURL\":\"https://{Host}/spec\",\"Auth\":{{\"Type\":\"basic\",\"UserName\":\"{User}\",\"Password\":\"one\"}}}}");
        yield return ApiPair("secrets only",
            $"{{{ApiBase},\"auth\":{{\"type\":\"Bearer\",\"token\":\"one\"}}}}",
            $"{{{ApiBase},\"auth\":{{\"type\":\"Bearer\",\"token\":\"rotated\",\"apiKeyValue\":\"rotated\",\"password\":\"rotated\"}}}}");
        yield return ApiPair("defaults",
            $"{{{ApiBase}}}",
            $"{{{ApiBase},\"timeoutSeconds\":30,\"allowPostQueries\":false,\"auth\":{{\"type\":\"None\",\"apiKeyLocation\":\"Header\"}}}}");
        yield return ApiPair("endpoint filter order",
            $"{{{ApiBase},\"endpointFilter\":{{\"includePathPatterns\":[\"/a\",\"/b\"]}}}}",
            $"{{{ApiBase},\"endpointFilter\":{{\"includePathPatterns\":[\"/b\",\"/a\"]}}}}");
    }

    public static IEnumerable<TestCaseData> OtherApiDefinitions()
    {
        var plain = $"{{{ApiBase}}}";
        yield return ApiPair("base URL path", plain, plain.Replace("/v1", "/v2", StringComparison.Ordinal));
        yield return ApiPair("spec URL", plain, plain.Replace("/spec", "/other-spec", StringComparison.Ordinal));
        yield return ApiPair("POST queries", plain, $"{{{ApiBase},\"allowPostQueries\":true}}");
        yield return ApiPair("timeout", plain, $"{{{ApiBase},\"timeoutSeconds\":90}}");
        yield return ApiPair("auth type", plain, $"{{{ApiBase},\"auth\":{{\"type\":\"Bearer\"}}}}");
        yield return ApiPair("key header name", $"{{{ApiBase},\"auth\":{{\"type\":\"ApiKey\",\"apiKeyName\":\"X-Key\"}}}}", $"{{{ApiBase},\"auth\":{{\"type\":\"ApiKey\",\"apiKeyName\":\"X-Other\"}}}}");
        yield return ApiPair("key location", $"{{{ApiBase},\"auth\":{{\"type\":\"ApiKey\"}}}}", $"{{{ApiBase},\"auth\":{{\"type\":\"ApiKey\",\"apiKeyLocation\":\"Query\"}}}}");
        yield return ApiPair("user name", $"{{{ApiBase},\"auth\":{{\"type\":\"Basic\",\"username\":\"{User}\"}}}}", $"{{{ApiBase},\"auth\":{{\"type\":\"Basic\",\"username\":\"admin_9e1b\"}}}}");
        yield return ApiPair("endpoint filter", plain, $"{{{ApiBase},\"endpointFilter\":{{\"excludePathPatterns\":[\"/admin\"]}}}}");
        yield return ApiPair("unknown property", plain, $"{{{ApiBase},\"proxyUrl\":\"https://proxy-2c1d.internal\"}}");
        yield return ApiPair("unknown auth property", plain, $"{{{ApiBase},\"auth\":{{\"audience\":\"other\"}}}}");
    }

    [TestCaseSource(nameof(SameApiDefinitions))]
    public void TheApiDefinition_IsTheSame_ForTheSameConfigurationWrittenOtherwise(string first, string second)
    {
        var policy = TestTransport.Policy();

        var one = policy.Inspect(DataSourceType.Api, null, first).Definition;
        var other = policy.Inspect(DataSourceType.Api, null, second).Definition;

        one.Should().NotBeNull();
        other.Should().Be(one);
    }

    [TestCaseSource(nameof(OtherApiDefinitions))]
    public void TheApiDefinition_Changes_WithAnyPropertyButASecret(string first, string second)
    {
        var policy = TestTransport.Policy();

        var one = policy.Inspect(DataSourceType.Api, null, first).Definition;
        var other = policy.Inspect(DataSourceType.Api, null, second).Definition;

        one.Should().NotBeNull();
        other.Should().NotBeNull();
        other.Should().NotBe(one);
    }

    [Test]
    public void TheApiDefinition_KeepsTheWholeBaseUrl_NamesTheLogin_AndHoldsNoSecret_WhateverTheVerdict()
    {
        var json = $"{{\"baseUrl\":\"http://{Host}/v1/data\",\"openApiSpecUrl\":\"https://{Host}/spec\",\"auth\":{{\"type\":\"Basic\",\"username\":\"{User}\",\"password\":\"{Password}\",\"token\":\"{Password}\",\"apiKeyValue\":\"{Password}\"}}}}";

        var verdict = TestTransport.Policy().Inspect(DataSourceType.Api, null, json);

        verdict.Verified.Should().BeFalse("BaseUrl is not https");
        var definition = verdict.Definition;
        definition.Should().NotBeNull();
        definition!.Login.Should().Be(User);
        definition.Settings.Should().Contain(new KeyValuePair<string, string>("BaseUrl", $"http://{Host}/v1/data"));
        definition.Settings.Should().Contain(new KeyValuePair<string, string>("Auth.Type", "Basic"));
        definition.Settings.Should().NotContain(x => x.Value.Contains(Password, StringComparison.Ordinal));
        definition.Settings.Select(x => x.Key).Should().NotContain(["Auth.Token", "Auth.ApiKeyValue", "Auth.Password"]);
        DataSourceApprovalDefinition.Canonical(DataSourceType.Api, null, definition, json).Should().NotContain(Password);
    }

    [Test]
    public void WithoutAUserName_TheApiLoginIsTheAuthenticationType()
    {
        var policy = TestTransport.Policy();

        policy.Inspect(DataSourceType.Api, null, $"{{{ApiBase},\"auth\":{{\"type\":\"Bearer\",\"token\":\"t\"}}}}").Definition!.Login.Should().Be("Bearer");
        policy.Inspect(DataSourceType.Api, null, $"{{{ApiBase}}}").Definition!.Login.Should().Be("None");
    }

    private static TestCaseData ApiPair(string what, string first, string second)
    {
        return new TestCaseData(first, second)
            .SetArgDisplayNames("Api", what);
    }

    private static TestCaseData Pair(DatabaseEngineType engine, string what, string first, string second)
    {
        return new TestCaseData(engine, first, second)
            .SetArgDisplayNames(engine.ToString(), what);
    }

    private static TestCaseData Case(DatabaseEngineType engine, string connectionString, string? failingOption)
    {
        return new TestCaseData(DataSourceType.Database, engine, connectionString, failingOption)
            .SetArgDisplayNames(engine.ToString(), Redacted(connectionString), failingOption ?? "verified");
    }

    private static TestCaseData Document(DataSourceType type, string json, string? failingOption)
    {
        return new TestCaseData(type, null, json, failingOption)
            .SetArgDisplayNames(type.ToString(), Redacted(json), failingOption ?? "verified");
    }

    // Test names never show the credential-shaped value.
    private static string Redacted(string connectionData)
    {
        return connectionData.Replace(Password, "<password>", StringComparison.Ordinal);
    }

    private static string Api(string baseUrl, string specUrl)
    {
        return $"{{\"baseUrl\":\"{baseUrl}\",\"openApiSpecUrl\":\"{specUrl}\",\"auth\":{{\"type\":\"Bearer\",\"token\":\"{Password}\"}}}}";
    }

    private sealed class FixedSqlClientInspector(SqlClientTransportSettings settings) : SqlClientTransportInspector(Core.Data.Enums.DatabaseEngineType.MSSQL)
    {
        protected override SqlClientTransportSettings Read(string connectionData)
        {
            return settings;
        }
    }
}
