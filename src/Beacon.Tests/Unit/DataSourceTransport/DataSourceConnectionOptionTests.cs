using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Google.Cloud.BigQuery.V2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Beacon.Connector.BigQuery;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.DataSourceTransport;

/// <summary>
/// The driver options Beacon does not accept in an Admin's connection, per engine, as each connector's option inspector
/// reads them with its driver's own parser; what lifts a refusal (<c>AllowHostCredentials</c> for host credentials,
/// <c>AllowInsecure</c> for all of them, never the host list); the optional host list; and stored connections, which keep
/// working with a Warning unless <c>EnforceOnExisting</c> is set. A refusal names the option, never a value.
/// </summary>
[TestFixture]
public class DataSourceConnectionOptionTests
{
    private const string Host = "leaky-host-7f3a.internal";

    private const string OtherHost = "other-host-9b1c.internal";

    private const string Marker = "marker-value-55e1";

    private static readonly string Password = TestCredentials.ProviderSecretKey;

    private static readonly string PostgreSql = $"Host={Host};Username=reader;Password={Password};SSL Mode=VerifyFull";

    private static readonly string SqlServer = $"Server={Host};User ID=reader;Password={Password}";

    private static readonly string MySql = $"Server={Host};Uid=reader;Pwd={Password};SslMode=VerifyFull";

    private static readonly string Snowflake = $"account={Host};user=reader;password={Password}";

    private const DataSourceConnectionOptionRisk Unsafe = DataSourceConnectionOptionRisk.Unsafe;

    private const DataSourceConnectionOptionRisk HostCredentials = DataSourceConnectionOptionRisk.HostCredentials;

    public static IEnumerable<TestCaseData> Refused()
    {
        yield return Database(DatabaseEngineType.PostgreSQL, $"{PostgreSql};Options=-c search_path={Marker}", "Options", Unsafe);
        yield return Database(DatabaseEngineType.PostgreSQL, $"{PostgreSql};Passfile=/srv/{Marker}", "Passfile", Unsafe);
        yield return Database(DatabaseEngineType.PostgreSQL, $"{PostgreSql};Include Error Detail=true", "Include Error Detail", Unsafe);
        yield return Database(DatabaseEngineType.PostgreSQL, $"{PostgreSql};Log Parameters=true", "Log Parameters", Unsafe);
        yield return Database(DatabaseEngineType.PostgreSQL, $"Host=/var/run/{Marker};Password={Password};SSL Mode=VerifyFull", "Host", HostCredentials);
        yield return Database(DatabaseEngineType.PostgreSQL, $"{PostgreSql};GSS Encryption Mode=Require", "GSS Encryption Mode", HostCredentials);
        yield return Database(DatabaseEngineType.PostgreSQL, $"Host={Host};Username=reader;SSL Mode=VerifyFull", "Password", HostCredentials);
        yield return Database(DatabaseEngineType.PostgreSQL, $"{PostgreSql};Require Auth=GSS", "Require Auth", HostCredentials);
        yield return Database(DatabaseEngineType.PostgreSQL, $"{PostgreSql};Require Auth=ScramSHA256,SSPI", "Require Auth", HostCredentials);
        yield return Database(DatabaseEngineType.PostgreSQL, $"{PostgreSql};Require Auth=!MD5", "Require Auth", HostCredentials);

        yield return Database(DatabaseEngineType.MySQL, $"{MySql};AllowLoadLocalInfile=true", "AllowLoadLocalInfile", Unsafe);
        yield return Database(DatabaseEngineType.MySQL, $"{MySql};AllowLoadLocalInfileInPath=/srv/{Marker}", "AllowLoadLocalInfileInPath", Unsafe);
        yield return Database(DatabaseEngineType.MySQL, $"{MySql};CommandInterceptors={Marker}", "CommandInterceptors", Unsafe);
        yield return Database(DatabaseEngineType.MySQL, $"{MySql};ExceptionInterceptors={Marker}", "ExceptionInterceptors", Unsafe);
        yield return Database(DatabaseEngineType.MySQL, $"{MySql};DefaultAuthenticationPlugin=authentication_kerberos_client", "DefaultAuthenticationPlugin", HostCredentials);
        yield return Database(DatabaseEngineType.MySQL, $"{MySql};OciConfigFile=/srv/{Marker}", "OciConfigFile", HostCredentials);
        yield return Database(DatabaseEngineType.MySQL, $"{MySql};CertificateStoreLocation=LocalMachine", "CertificateStoreLocation", HostCredentials);
        yield return Database(DatabaseEngineType.MySQL, $"{MySql};Protocol=Unix", "ConnectionProtocol", HostCredentials);
        yield return Database(DatabaseEngineType.MySQL, $"Server={Host};Uid=reader;SslMode=VerifyFull", "Password", HostCredentials);

        yield return Database(DatabaseEngineType.MSSQL, $"{SqlServer};AttachDBFilename=/srv/{Marker}.mdf", "AttachDBFilename", Unsafe);
        yield return Database(DatabaseEngineType.MSSQL, $"{SqlServer};User Instance=true", "User Instance", Unsafe);
        yield return Database(DatabaseEngineType.MSSQL, $"Server={Host};Authentication=Active Directory Interactive", "Authentication", Unsafe);
        yield return Database(DatabaseEngineType.MSSQL, $"Server={Host};Integrated Security=true", "Integrated Security", HostCredentials);
        yield return Database(DatabaseEngineType.MSSQL, $"Server={Host};Trusted_Connection=yes", "Integrated Security", HostCredentials);
        yield return Database(DatabaseEngineType.MSSQL, $"Server={Host};Authentication=Active Directory Managed Identity", "Authentication", HostCredentials);
        yield return Database(DatabaseEngineType.MSSQL, $"Server={Host};Authentication=Active Directory Default", "Authentication", HostCredentials);
        yield return Database(DatabaseEngineType.MSSQL, $"Server={Host};Authentication=Active Directory Workload Identity", "Authentication", HostCredentials);
        yield return Database(DatabaseEngineType.MSSQL, $"{SqlServer};Column Encryption Setting=Enabled", "Column Encryption Setting", HostCredentials);
        yield return Database(DatabaseEngineType.MSSQL, $"Server=np:\\\\{Host}\\pipe\\sql\\query;User ID=reader;Password={Password}", "Data Source", HostCredentials);
        yield return Database(DatabaseEngineType.MSSQL, $"Server=(localdb)\\{Marker};User ID=reader;Password={Password}", "Data Source", HostCredentials);
        yield return Database(DatabaseEngineType.AzureSynapse, $"Server={Host};Authentication=Active Directory Managed Identity", "Authentication", HostCredentials);
        yield return Database(DatabaseEngineType.AzureSynapse, $"{SqlServer};AttachDBFilename=/srv/{Marker}.mdf", "AttachDBFilename", Unsafe);

        yield return Database(DatabaseEngineType.Snowflake, $"{Snowflake};client_config_file=/srv/{Marker}", "client_config_file", Unsafe);
        yield return Database(DatabaseEngineType.Snowflake, $"{Snowflake};disable_saml_url_check=true", "disable_saml_url_check", Unsafe);
        yield return Database(DatabaseEngineType.Snowflake, $"account={Host};user=reader;authenticator=externalbrowser", "authenticator", Unsafe);
        yield return Database(DatabaseEngineType.Snowflake, $"account={Host};authenticator=workload_identity", "authenticator", HostCredentials);
        yield return Database(DatabaseEngineType.Snowflake, $"account={Host};user=reader;authenticator=snowflake_jwt;private_key_file=/srv/{Marker}", "private_key_file", HostCredentials);

        yield return Document(DataSourceType.BigQuery, BigQuery(Credential("external_account", ("token_url", $"https://{Host}/{Marker}"))), "ServiceAccountJson", Unsafe);
        yield return Document(DataSourceType.BigQuery, BigQuery(Credential("authorized_user", ("refresh_token", Marker))), "ServiceAccountJson", Unsafe);
        yield return Document(DataSourceType.BigQuery, BigQuery(Credential("impersonated_service_account", ("service_account_impersonation_url", $"https://{Host}"))), "ServiceAccountJson", Unsafe);
        yield return Document(DataSourceType.BigQuery, BigQuery($"[\"{Marker}\"]"), "ServiceAccountJson", Unsafe);
        yield return Document(DataSourceType.BigQuery, BigQuery($"not json {Marker}"), "ServiceAccountJson", Unsafe);
        yield return Document(DataSourceType.BigQuery, BigQuery(null), "ServiceAccountJson", HostCredentials);
        yield return Document(DataSourceType.BigQuery, BigQuery(Credential("service_account", ("token_uri", $"https://{Host}/{Marker}"))), "ServiceAccountJson", Unsafe);
        yield return Document(DataSourceType.BigQuery, BigQuery(Credential("service_account", ("Token_Uri", $"https://{Host}/{Marker}"))), "ServiceAccountJson", Unsafe);
        yield return Document(DataSourceType.BigQuery, BigQuery(Credential("service_account", ("token_uri", "http://oauth2.googleapis.com/token"))), "ServiceAccountJson", Unsafe);
        yield return Document(DataSourceType.BigQuery, BigQuery(Credential("service_account", ("universe_domain", Host))), "ServiceAccountJson", Unsafe);
        yield return Document(DataSourceType.BigQuery, BigQuery(Credential("service_account", ("auth_uri", $"https://{Host}/{Marker}"))), "ServiceAccountJson", Unsafe);
        yield return Document(DataSourceType.BigQuery, BigQuery(Credential("service_account", ("client_x509_cert_url", $"https://www.googleapis.com.{Host}/{Marker}"))), "ServiceAccountJson", Unsafe);

        yield return Document(DataSourceType.CloudWatch, CloudWatch($"{Host}/{Marker}", key: true), "Region", Unsafe);
        yield return Document(DataSourceType.CloudWatch, CloudWatch("eu-west-1", key: false, profile: Marker), "ProfileName", HostCredentials);
        yield return Document(DataSourceType.CloudWatch, CloudWatch("eu-west-1", key: false), "AccessKeyId", HostCredentials);

        yield return Document(DataSourceType.Databricks, Databricks($"https://{Host}"), "Host", Unsafe);
        yield return Document(DataSourceType.Databricks, Databricks($"{Host}:443"), "Host", Unsafe);
        yield return Document(DataSourceType.Databricks, Databricks($"{Host}/{Marker}?"), "Host", Unsafe);
        yield return Document(DataSourceType.Databricks, Databricks($"{Marker}@{Host}"), "Host", Unsafe);
    }

    public static IEnumerable<TestCaseData> Accepted()
    {
        yield return Database(DatabaseEngineType.PostgreSQL, PostgreSql);
        yield return Database(DatabaseEngineType.PostgreSQL, $"Host={Host},{OtherHost}:5433;Username=reader;SSL Mode=VerifyFull;SSL Certificate=/etc/beacon/client.pem;SSL Key=/etc/beacon/client.key");
        yield return Database(DatabaseEngineType.PostgreSQL, $"{PostgreSql};Search Path=sales;Include Error Detail=false");
        yield return Database(DatabaseEngineType.PostgreSQL, $"{PostgreSql};Require Auth=ScramSHA256");
        yield return Database(DatabaseEngineType.PostgreSQL, $"{PostgreSql};Require Auth=!GSS,!SSPI");
        yield return Database(DatabaseEngineType.MySQL, MySql);
        yield return Database(DatabaseEngineType.MySQL, $"{MySql};AllowLoadLocalInfile=false;SslCa=/etc/ssl/beacon-ca.pem;Protocol=Tcp");
        yield return Database(DatabaseEngineType.MSSQL, SqlServer);
        yield return Database(DatabaseEngineType.MSSQL, $"Server=tcp:{Host},1433;Authentication=Active Directory Service Principal;User ID=app-id;Password={Password};Failover Partner={OtherHost}");
        yield return Database(DatabaseEngineType.AzureSynapse, $"{SqlServer};Authentication=SqlPassword");
        yield return Database(DatabaseEngineType.Snowflake, Snowflake);
        yield return Database(DatabaseEngineType.Snowflake, $"account={Host};user=reader;authenticator=snowflake_jwt;private_key={Password}");
        yield return Document(DataSourceType.BigQuery, BigQuery(Credential("service_account", ("private_key", Password))));
        yield return Document(DataSourceType.BigQuery, BigQuery(GoogleServiceAccountKey()));
        yield return Document(DataSourceType.CloudWatch, CloudWatch("us-gov-west-1", key: true));
        yield return Document(DataSourceType.CloudWatch, CloudWatch("eusc-de-east-1", key: true));
        yield return Document(DataSourceType.CloudWatch, CloudWatch("cn-northwest-1", key: true));
        yield return Document(DataSourceType.CloudWatch, CloudWatch("us-isob-east-1", key: true));
        yield return Document(DataSourceType.Databricks, Databricks("adb-1234567890123456.7.azuredatabricks.net"));
    }

    [TestCaseSource(nameof(Refused))]
    public void ANewConnection_WithTheOption_IsRefused_NamingTheOptionOnly(DataSourceType type, DatabaseEngineType? engine, string connectionData, string option, DataSourceConnectionOptionRisk risk)
    {
        var act = () => TestTransport.Policy().EnsureNewConnectionAllowed(type, engine, connectionData);

        var message = act.Should().Throw<DataSourceTransportException>().Which.Message;
        message.Should().Contain($"option {option}.");
        message.Should().NotContainAny(Host, Password, Marker, "/srv/");
    }

    [TestCaseSource(nameof(Refused))]
    public void AllowHostCredentials_LiftsOnlyTheHostCredentialRefusals(DataSourceType type, DatabaseEngineType? engine, string connectionData, string option, DataSourceConnectionOptionRisk risk)
    {
        var act = () => TestTransport.Policy(allowHostCredentials: true).EnsureNewConnectionAllowed(type, engine, connectionData);

        if (risk == HostCredentials)
        {
            act.Should().NotThrow();
            return;
        }

        act.Should().Throw<DataSourceTransportException>().Which.Message.Should().Contain(option);
    }

    [TestCaseSource(nameof(Refused))]
    public void AllowInsecure_LiftsEveryOptionRefusal(DataSourceType type, DatabaseEngineType? engine, string connectionData, string option, DataSourceConnectionOptionRisk risk)
    {
        var act = () => TestTransport.Policy(allowInsecure: true).EnsureNewConnectionAllowed(type, engine, connectionData);

        act.Should().NotThrow();
    }

    [TestCaseSource(nameof(Accepted))]
    public void ANewConnection_WithItsOwnCredentials_IsAccepted(DataSourceType type, DatabaseEngineType? engine, string connectionData)
    {
        var act = () => TestTransport.Policy().EnsureNewConnectionAllowed(type, engine, connectionData);

        act.Should().NotThrow();
    }

    [TestCase(DatabaseEngineType.PostgreSQL, "Host={0};Username=reader;Password=p;SSL Mode=VerifyFull")]
    [TestCase(DatabaseEngineType.PostgreSQL, "Host=db-1.example.com,{0}:5433;Username=reader;Password=p;SSL Mode=VerifyFull")]
    [TestCase(DatabaseEngineType.MySQL, "Server=db-1.example.com,{0};Uid=reader;Pwd=p;SslMode=VerifyFull")]
    [TestCase(DatabaseEngineType.MySQL, "Server=(address=db-1.example.com,priority=100),(address={0},priority=50);Uid=reader;Pwd=p;SslMode=VerifyFull")]
    [TestCase(DatabaseEngineType.MSSQL, "Server=tcp:{0},1433;User ID=reader;Password=p")]
    [TestCase(DatabaseEngineType.MSSQL, "Server=db-1.example.com\\sales;User ID=reader;Password=p;Failover Partner={0}")]
    [TestCase(DatabaseEngineType.Snowflake, "account=acct;host={0};user=reader;password=p")]
    [TestCase(DatabaseEngineType.Snowflake, "host=db-1.example.com;account=acct;user=reader;password=p;useproxy=true;proxyhost={0}")]
    [TestCase(DatabaseEngineType.Snowflake, "host=db-1.example.com;account=acct;authenticator=oauth_client_credentials;oauthclientid=c;oauthclientsecret=s;role=r;oauthtokenrequesturl=https://{0}/oauth/token")]
    [TestCase(DatabaseEngineType.Snowflake, "host=db-1.example.com;account=acct;authenticator=oauth;token=t;oauthauthorizationurl=https://{0}/authorize")]
    [TestCase(DatabaseEngineType.Snowflake, "host=db-1.example.com;account=acct;authenticator=oauth;token=t;oauthredirecturi=https://{0}/callback")]
    public void WithAHostList_EveryHostTheConnectionReaches_MustBeListed(DatabaseEngineType engine, string template)
    {
        var listed = TestTransport.Policy(allowedHosts: ["*.example.com"]);

        var allowed = () => listed.EnsureNewConnectionAllowed(DataSourceType.Database, engine, string.Format(template, "db-2.example.com"));
        var refused = () => listed.EnsureNewConnectionAllowed(DataSourceType.Database, engine, string.Format(template, Host));

        allowed.Should().NotThrow();
        var message = refused.Should().Throw<DataSourceTransportException>().Which.Message;
        message.Should().Contain("Beacon:DataSources:AllowedHosts does not list").And.NotContain(Host);
    }

    [Test]
    public void AHostListEntry_MatchesItsHost_OrTheHostsBelowAWildcard()
    {
        DataSourceConnectionOptionCheck.IsAllowed("DB.Example.com.", ["db.example.com"]).Should().BeTrue();
        DataSourceConnectionOptionCheck.IsAllowed("a.b.example.com", ["*.example.com"]).Should().BeTrue();
        DataSourceConnectionOptionCheck.IsAllowed("example.com", ["*.example.com"]).Should().BeFalse("a wildcard covers the hosts below the domain only");
        DataSourceConnectionOptionCheck.IsAllowed("badexample.com", ["*.example.com"]).Should().BeFalse();
        DataSourceConnectionOptionCheck.IsAllowed("example.com.evil.test", ["example.com"]).Should().BeFalse();
        DataSourceConnectionOptionCheck.IsAllowed("[2001:db8::1]", ["2001:db8::1"]).Should().BeTrue();
        DataSourceConnectionOptionCheck.IsAllowed(string.Empty, ["*.example.com"]).Should().BeFalse();
    }

    [TestCase("db.example.com/")]
    [TestCase("db.example.com/.example.com")]
    [TestCase("db.example.com?.example.com")]
    [TestCase("db.example.com#.example.com")]
    [TestCase("reader@db.example.com")]
    [TestCase("db.example.com:5432")]
    [TestCase("db example.com")]
    [TestCase("db.exämple.com")]
    [TestCase("https://db.example.com")]
    public void AHostThatIsNotABareHostName_MatchesNoEntry(string host)
    {
        DataSourceConnectionOptionCheck.IsAllowed(host, ["*.example.com", "db.example.com", host]).Should().BeFalse();
    }

    [Test]
    public void IpAddresses_AreComparedInTheirCanonicalForm()
    {
        DataSourceConnectionOptionCheck.IsAllowed("10.1", ["10.0.0.1"]).Should().BeTrue();
        DataSourceConnectionOptionCheck.IsAllowed("10.0.0.1", ["10.1"]).Should().BeTrue();
        DataSourceConnectionOptionCheck.IsAllowed("2001:DB8:0:0::1", ["[2001:db8::1]"]).Should().BeTrue();
        DataSourceConnectionOptionCheck.IsAllowed("10.0.0.2", ["10.0.0.1"]).Should().BeFalse();
        DataSourceConnectionOptionCheck.IsAllowed("1.2.3.4", ["*.2.3.4"]).Should().BeFalse("a wildcard covers host names only");
    }

    [TestCase("account=db-2.example.com/;user=reader;password=p")]
    [TestCase("account=db-2.example.com?;user=reader;password=p")]
    [TestCase("account=db-2.example.com#;user=reader;password=p")]
    public void WithAHostList_ASnowflakeAccountThatIsNoHostName_IsRefused(string connectionString)
    {
        var act = () => TestTransport.Policy(allowedHosts: ["*.snowflakecomputing.com"])
            .EnsureNewConnectionAllowed(DataSourceType.Database, DatabaseEngineType.Snowflake, connectionString);

        act.Should().Throw<DataSourceTransportException>().Which.Message.Should().Contain("does not list");
    }

    [Test]
    public void WithAHostList_ASnowflakeAccountHost_IsComposedAsTheDriverComposesIt()
    {
        var listed = TestTransport.Policy(allowedHosts: ["my-acct.snowflakecomputing.com"]);

        var dashed = () => listed.EnsureNewConnectionAllowed(DataSourceType.Database, DatabaseEngineType.Snowflake, "account=my_acct;user=reader;password=p");
        var underscored = () => listed.EnsureNewConnectionAllowed(DataSourceType.Database, DatabaseEngineType.Snowflake, "account=my_acct;allowunderscoresinhost=true;user=reader;password=p");

        dashed.Should().NotThrow();
        underscored.Should().Throw<DataSourceTransportException>().Which.Message.Should().Contain("does not list");
    }

    [Test]
    public void WithAHostList_ASnowflakeOAuthUrlThatIsNotAnAbsoluteUrl_IsRefused()
    {
        var act = () => TestTransport.Policy(allowedHosts: ["*.example.com"]).EnsureNewConnectionAllowed(
            DataSourceType.Database,
            DatabaseEngineType.Snowflake,
            "host=db-1.example.com;account=acct;authenticator=oauth;token=t;oauthtokenrequesturl=db-2.example.com/token");

        act.Should().Throw<DataSourceTransportException>().Which.Message.Should().Contain("cannot tell which hosts");
    }

    [Test]
    public void WithAHostList_ABigQueryKey_ReachesItsTokenHost()
    {
        var key = BigQuery(GoogleServiceAccountKey());

        var unlisted = () => TestTransport.Policy(allowedHosts: ["db.example.com"]).EnsureNewConnectionAllowed(DataSourceType.BigQuery, null, key);
        var listed = () => TestTransport.Policy(allowedHosts: ["oauth2.googleapis.com"]).EnsureNewConnectionAllowed(DataSourceType.BigQuery, null, key);

        unlisted.Should().Throw<DataSourceTransportException>().Which.Message.Should().Contain("does not list");
        listed.Should().NotThrow();
    }

    [Test]
    public void WithAHostList_AConnectionWhoseHostsCannotBeTold_IsRefused()
    {
        var listed = TestTransport.Policy(allowedHosts: ["*.example.com"]);

        var dnsSrv = () => listed.EnsureNewConnectionAllowed(DataSourceType.Database, DatabaseEngineType.MySQL, "Server=db.example.com;DnsSrv=true;Uid=reader;Pwd=p;SslMode=VerifyFull");

        dnsSrv.Should().Throw<DataSourceTransportException>().Which.Message.Should().Contain("cannot tell which hosts");
    }

    [Test]
    public void WithAHostList_ATypeWithoutAnOptionCheck_IsRefused_EvenUnderAllowInsecure()
    {
        var policy = new DataSourceTransportPolicy(
            TestTransport.Inspectors(),
            [],
            Options.Create(new DataSourceConnectionOptions { AllowedHosts = ["*.example.com"] }),
            Options.Create(new DataSourceTransportOptions { AllowInsecure = true }),
            NullLogger<DataSourceTransportPolicy>.Instance);

        var act = () => policy.EnsureNewConnectionAllowed(DataSourceType.Database, DatabaseEngineType.PostgreSQL, "Host=db.example.com;Password=p");

        act.Should().Throw<DataSourceTransportException>().Which.Message.Should().Contain("cannot tell which hosts");
    }

    [Test]
    public void WithAHostList_AllowInsecure_StillRefusesAnUnlistedHost()
    {
        var act = () => TestTransport.Policy(allowInsecure: true, allowedHosts: ["db.example.com"])
            .EnsureNewConnectionAllowed(DataSourceType.Database, DatabaseEngineType.PostgreSQL, $"Host={Host};Options=-c x=y");

        act.Should().Throw<DataSourceTransportException>().Which.Message.Should().Contain("does not list");
    }

    [Test]
    public void WithAHostList_ConnectorsThatReachOnlyTheirVendorsEndpoints_AreAccepted()
    {
        var listed = TestTransport.Policy(allowedHosts: ["db.example.com"]);

        var bigQuery = () => listed.EnsureNewConnectionAllowed(DataSourceType.BigQuery, null, BigQuery(Credential("service_account", ("private_key", Password))));
        var cloudWatch = () => listed.EnsureNewConnectionAllowed(DataSourceType.CloudWatch, null, CloudWatch("eu-west-1", key: true));
        var databricks = () => listed.EnsureNewConnectionAllowed(DataSourceType.Databricks, null, Databricks(Host));

        bigQuery.Should().NotThrow();
        cloudWatch.Should().NotThrow();
        databricks.Should().Throw<DataSourceTransportException>("a Databricks workspace is the host its configuration names");
    }

    [Test]
    public void AStoredConnection_WithARefusedOption_KeepsWorking_WithOneWarning_WithoutValues()
    {
        var log = new LogRecorder();
        var policy = TestTransport.Policy(logger: log.For<DataSourceTransportPolicy>());
        var connection = $"{PostgreSql};Passfile=/srv/{Marker}";

        policy.EnsureStoredConnectionAllowed(Stored(7), connection);
        policy.EnsureStoredConnectionAllowed(Stored(7), connection);

        var warning = log.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Which;
        warning.Message.Should().Contain("Data source 7").And.Contain("Passfile").And.Contain("EnforceOnExisting");
        log.Entries.Should().OnlyContain(x => !x.FullText.Contains(Host) && !x.FullText.Contains(Password) && !x.FullText.Contains(Marker));
    }

    [Test]
    public void AStoredConnection_WithARefusedOption_IsRefused_UnderEnforceOnExisting()
    {
        var log = new LogRecorder();
        var policy = TestTransport.Policy(enforceOnExisting: true, logger: log.For<DataSourceTransportPolicy>());
        var connection = $"{MySql};AllowLoadLocalInfile=true";

        var act = () => policy.EnsureStoredConnectionAllowed(Stored(8, DatabaseEngineType.MySQL), connection);

        act.Should().Throw<DataSourceTransportException>().Which.Message.Should().Contain("AllowLoadLocalInfile").And.NotContain(Host);
        act.Should().Throw<DataSourceTransportException>();
        log.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Which.Message.Should().Contain("Data source 8 was refused");
    }

    [Test]
    public void AStoredConnection_ToAnUnlistedHost_KeepsWorking_WithAWarning()
    {
        var log = new LogRecorder();
        var policy = TestTransport.Policy(logger: log.For<DataSourceTransportPolicy>(), allowedHosts: ["db.example.com"]);

        policy.EnsureStoredConnectionAllowed(Stored(9), PostgreSql);

        log.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Which.Message
            .Should().Contain("Data source 9").And.Contain("AllowedHosts").And.NotContain(Host);
    }

    [Test]
    public void AHostManagedSource_SkipsTheOptionCheck_EvenUnderEnforceOnExisting()
    {
        var log = new LogRecorder();
        var policy = TestTransport.Policy(enforceOnExisting: true, logger: log.For<DataSourceTransportPolicy>());
        var source = Stored(10, DatabaseEngineType.MSSQL, hostManagedKey: "host:warehouse");

        var act = () => policy.EnsureStoredConnectionAllowed(source, $"Server={Host};Integrated Security=true;Encrypt=Strict");

        act.Should().NotThrow();
        log.Entries.Should().NotContain(x => x.Level >= LogLevel.Warning);
    }

    [Test]
    public void AHostManagedSource_ToAnUnlistedHost_KeepsTheHostListWarning()
    {
        var log = new LogRecorder();
        var policy = TestTransport.Policy(logger: log.For<DataSourceTransportPolicy>(), allowedHosts: ["db.example.com"]);
        var source = Stored(11, DatabaseEngineType.MSSQL, hostManagedKey: "host:warehouse");

        policy.EnsureStoredConnectionAllowed(source, $"Server={Host};Integrated Security=true;Encrypt=Strict");

        log.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Which.Message
            .Should().Contain("Data source 11").And.Contain("AllowedHosts").And.NotContain("Integrated Security");
    }

    [Test]
    public void AStoredSource_NotSavedYet_IsCheckedAsANewConnection()
    {
        var act = () => TestTransport.Policy().EnsureStoredConnectionAllowed(Stored(0), $"{PostgreSql};Passfile=/srv/{Marker}");

        act.Should().Throw<DataSourceTransportException>().Which.Message.Should().Contain("Passfile");
    }

    [Test]
    public void TheHostList_RefusesEntriesThatAreNotHostNames()
    {
        var validator = new DataSourceConnectionOptionsValidator();

        validator.Validate(null, new DataSourceConnectionOptions { AllowedHosts = ["db.example.com", "*.example.com", "10.0.0.5", "[2001:db8::1]"] })
            .Succeeded.Should().BeTrue();
        var result = validator.Validate(null, new DataSourceConnectionOptions { AllowedHosts = ["db.example.com", "https://db.example.com", "db.example.com:5432", "*", " "] });
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("entries 1, 2, 3, 4").And.NotContain("https://");
    }

    [Test]
    public void TheBigQueryClient_IsMadeOnlyFromAServiceAccountKey()
    {
        var provider = new ClientMakingBigQueryProvider();

        var external = () => provider.Make(new BigQueryConfiguration { ProjectId = "p", ServiceAccountJson = Credential("external_account", ("token_url", $"https://{Host}")) });
        using var client = provider.Make(new BigQueryConfiguration { ProjectId = "p", ServiceAccountJson = ServiceAccountKey() });

        external.Should().Throw<BeaconException>().Which.Message.Should().Contain("service account").And.NotContain(Host);
        client.ProjectId.Should().Be("p");
    }

    [Test]
    public void TheBigQueryClient_IsNotMadeFromAKeyWhoseTokenEndpointIsNotGoogles()
    {
        var provider = new ClientMakingBigQueryProvider();
        var key = JsonSerializer.Deserialize<Dictionary<string, string>>(ServiceAccountKey())!;
        key["token_uri"] = $"https://{Host}/token";

        var act = () => provider.Make(new BigQueryConfiguration { ProjectId = "p", ServiceAccountJson = JsonSerializer.Serialize(key) });

        act.Should().Throw<BeaconException>().Which.Message.Should().Contain("Google").And.NotContain(Host);
    }

    private static TestCaseData Database(DatabaseEngineType engine, string connectionString, string option, DataSourceConnectionOptionRisk risk)
    {
        return new TestCaseData(DataSourceType.Database, engine, connectionString, option, risk).SetArgDisplayNames(engine.ToString(), option, risk.ToString());
    }

    private static TestCaseData Database(DatabaseEngineType engine, string connectionString)
    {
        return new TestCaseData(DataSourceType.Database, engine, connectionString).SetArgDisplayNames(engine.ToString(), connectionString.Length.ToString());
    }

    private static TestCaseData Document(DataSourceType type, string connectionData, string option, DataSourceConnectionOptionRisk risk)
    {
        return new TestCaseData(type, null, connectionData, option, risk).SetArgDisplayNames(type.ToString(), option, risk.ToString());
    }

    private static TestCaseData Document(DataSourceType type, string connectionData)
    {
        return new TestCaseData(type, null, connectionData).SetArgDisplayNames(type.ToString(), connectionData.Length.ToString());
    }

    private static string Credential(string type, params (string Key, string Value)[] values)
    {
        var document = new Dictionary<string, string> { ["type"] = type };
        foreach (var value in values)
        {
            document[value.Key] = value.Value;
        }

        return JsonSerializer.Serialize(document);
    }

    // A service account key Google's library accepts (a fresh RSA key, never a real one).
    private static string ServiceAccountKey()
    {
        using var rsa = RSA.Create(2048);

        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account",
            ["project_id"] = "p",
            ["private_key_id"] = "k",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["client_email"] = "reader@p.iam.gserviceaccount.com",
            ["client_id"] = "1",
            ["token_uri"] = "https://oauth2.googleapis.com/token"
        });
    }

    // A key's Google URLs as Google writes them (no private key: the option check never reads it).
    private static string GoogleServiceAccountKey()
    {
        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account",
            ["project_id"] = "p",
            ["client_email"] = "reader@p.iam.gserviceaccount.com",
            ["auth_uri"] = "https://accounts.google.com/o/oauth2/auth",
            ["token_uri"] = "https://oauth2.googleapis.com/token",
            ["auth_provider_x509_cert_url"] = "https://www.googleapis.com/oauth2/v1/certs",
            ["client_x509_cert_url"] = "https://www.googleapis.com/robot/v1/metadata/x509/reader%40p.iam.gserviceaccount.com",
            ["universe_domain"] = "googleapis.com"
        });
    }

    private static string BigQuery(string? serviceAccountJson)
    {
        return JsonSerializer.Serialize(new { ProjectId = "p", ServiceAccountJson = serviceAccountJson });
    }

    private static string CloudWatch(string region, bool key, string? profile = null)
    {
        return JsonSerializer.Serialize(new
        {
            Region = region,
            AccessKeyId = key ? "AKIAEXAMPLE" : null,
            SecretAccessKey = key ? Password : null,
            ProfileName = profile
        });
    }

    private static string Databricks(string host)
    {
        return JsonSerializer.Serialize(new { Host = host, HttpPath = "/sql/1.0/warehouses/w", Token = Password });
    }

    private static DataSource Stored(int id, DatabaseEngineType engine = DatabaseEngineType.PostgreSQL, string? hostManagedKey = null)
    {
        return new DataSource
        {
            Id = id,
            HostManagedKey = hostManagedKey,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = engine,
            EncryptedConnectionData = "stored"
        };
    }

    private sealed class ClientMakingBigQueryProvider()
        : BigQueryProvider(
            TestTransport.Resolver(new Moq.Mock<Beacon.Core.Services.IEncryptionService>().Object),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            NullLogger<BigQueryProvider>.Instance)
    {
        public BigQueryClient Make(BigQueryConfiguration config) => CreateClient(config);
    }
}
