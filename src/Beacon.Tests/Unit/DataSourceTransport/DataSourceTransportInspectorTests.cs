using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
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

        // TLS-only SDKs pass whatever their configuration says.
        yield return Document(DataSourceType.Databricks, $"{{\"host\":\"{Host}\",\"token\":\"{Password}\"}}", null);
        yield return Document(DataSourceType.BigQuery, $"{{\"projectId\":\"{Database}\",\"credentialsJson\":\"{Password}\"}}", null);
        yield return Document(DataSourceType.CloudWatch, $"{{\"region\":\"eu-west-1\",\"secretAccessKey\":\"{Password}\"}}", null);

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

    [Test]
    public void TheVerdict_NamesTheTarget_ForTheCallerThatComparesIt()
    {
        var postgres = TestTransport.Policy().Inspect(DataSourceType.Database, DatabaseEngineType.PostgreSQL, $"Host={Host};Port=6432;Database={Database};SSL Mode=VerifyFull");
        var sqlServer = TestTransport.Policy().Inspect(DataSourceType.Database, DatabaseEngineType.MSSQL, $"Server=tcp:{Host},1433;Initial Catalog={Database}");

        postgres.Target.Should().Be(new Core.Services.Providers.DataSourceTransportTarget(Host, 6432, Database));
        sqlServer.Target.Should().Be(new Core.Services.Providers.DataSourceTransportTarget(Host, 1433, Database));
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
}
