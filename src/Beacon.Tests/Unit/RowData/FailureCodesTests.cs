using System.Data.Common;
using System.Net;
using System.Reflection;
using Amazon.CloudWatchLogs;
using Beacon.Connector.CloudWatch;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;
using Beacon.Core.Helpers;
using Beacon.Core.HostData;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Google;
using Google.Apis.Requests;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.RowData;

/// <summary>
/// What a failure log may name (§1.11): the exception's type, the error code of the driver or service — SQL Server's
/// number, a SQLSTATE, SQLite's code, an HTTP status, a service's own code (Databricks), Google's API code (BigQuery), AWS's
/// error code (CloudWatch) — the types it wraps and the method that threw it; never a message, and a service code only
/// when it is a plain token.
/// </summary>
[TestFixture]
public class FailureCodesTests
{
    private const string Secret = TestCredentials.ProviderSecretKey;

    private static IEnumerable<TestCaseData> Codes()
    {
        yield return new TestCaseData(SqlServerError(245), "245").SetName("SqlServer_Number");
        yield return new TestCaseData(new FakeDbException(Secret, "22P02"), "22P02").SetName("DbException_SqlState");
        yield return new TestCaseData(new SqliteException(Secret, 1), "1").SetName("Sqlite_Code");
        yield return new TestCaseData(new HttpRequestException(Secret, null, HttpStatusCode.ServiceUnavailable), "503").SetName("Http_Status");
        yield return new TestCaseData(new DataSourceServiceException(Secret, "INVALID_PARAMETER_VALUE"), "INVALID_PARAMETER_VALUE").SetName("Service_Code");
        yield return new TestCaseData(new DataSourceServiceException(Secret, $"bad value {Secret}"), null).SetName("Service_CodeThatIsNotAToken");
        yield return new TestCaseData(new GoogleApiException("bigquery", Secret) { Error = new RequestError { Code = 400, Message = Secret } }, "400").SetName("Google_ErrorCode");
        yield return new TestCaseData(new GoogleApiException("bigquery", Secret) { HttpStatusCode = HttpStatusCode.Forbidden }, "403").SetName("Google_HttpStatus");
        yield return new TestCaseData(new AmazonCloudWatchLogsException(Secret) { ErrorCode = "ThrottlingException" }, "ThrottlingException").SetName("Aws_ErrorCode");
        yield return new TestCaseData(new AmazonCloudWatchLogsException(Secret) { StatusCode = HttpStatusCode.BadRequest }, "400").SetName("Aws_HttpStatus");
        yield return new TestCaseData(new InvalidOperationException(Secret, new FakeDbException(Secret, "23505")), "23505").SetName("Wrapped_InnerCode");
        yield return new TestCaseData(new InvalidOperationException(Secret), null).SetName("NoCode");
    }

    [TestCaseSource(nameof(Codes))]
    public void ErrorCodeOf_ReadsTheCodeNeverTheMessage(Exception exception, string? code)
    {
        FailureCodes.ErrorCodeOf(exception).Should().Be(code);
        FailureCodes.Describe(exception).Should().NotContain(Secret);
    }

    [Test]
    public void Describe_NamesTheTypeCodeInnerTypesAndThrowingMethod()
    {
        var exception = Caught(ThrowWrapped);

        FailureCodes.Describe(exception).Should().Be(
            "InvalidOperationException (error code 22P02, inner FakeDbException > IOException, at FailureCodesTests.ThrowWrapped)");
    }

    [Test]
    public async Task Describe_NamesAnAsyncMethodByItsOwnName()
    {
        var exception = await CaughtAsync(ThrowFromAsyncAsync);

        FailureCodes.Describe(exception).Should().Be("FakeDbException (error code 42P01, at FailureCodesTests.ThrowFromAsyncAsync)");
    }

    [Test]
    public void Describe_OfAnExceptionNeverThrown_HasNoMethod()
    {
        FailureCodes.Describe(new InvalidOperationException(Secret)).Should().Be("InvalidOperationException (error code none)");
    }

    // The branches as a query path logs them: SQL Server's number and a SQLSTATE through the database provider, an AWS
    // error code through the CloudWatch connector.
    [Test]
    public async Task TheDatabaseProvider_LogsSqlServersNumber()
    {
        var log = new LogRecorder();

        await FailingProvider(SqlServerError(8114), log).ExecuteQueryAsync(Source(), "SELECT 1", [], CancellationToken.None);

        log.Entries.Should().ContainSingle()
            .Which.Message.Should().StartWith("Query execution failed for database data source 7 with SqlException (error code 8114, at ");
        log.Contains(Secret).Should().BeFalse();
    }

    [Test]
    public async Task TheDatabaseProvider_LogsTheSqlState()
    {
        var log = new LogRecorder();

        await FailingProvider(new FakeDbException(Secret, "22003"), log).ExecuteQueryAsync(Source(), "SELECT 1", [], CancellationToken.None);

        log.Entries.Should().ContainSingle()
            .Which.Message.Should().StartWith("Query execution failed for database data source 7 with FakeDbException (error code 22003, at ");
        log.Contains(Secret).Should().BeFalse();
    }

    [Test]
    public async Task TheDatabaseProvider_LogsAWrappedHttpStatus()
    {
        var log = new LogRecorder();
        var failure = new InvalidOperationException(Secret, new HttpRequestException(Secret, null, HttpStatusCode.GatewayTimeout));

        await FailingProvider(failure, log).ExecuteQueryAsync(Source(), "SELECT 1", [], CancellationToken.None);

        log.Entries.Should().ContainSingle()
            .Which.Message.Should().StartWith("Query execution failed for database data source 7 with InvalidOperationException (error code 504, inner HttpRequestException, at ");
        log.Contains(Secret).Should().BeFalse();
    }

    [Test]
    public async Task TheCloudWatchConnector_LogsTheAwsErrorCode()
    {
        var log = new LogRecorder();
        var encryption = new Mock<IEncryptionService>();
        encryption
            .Setup(x => x.Decrypt(It.IsAny<string>()))
            .Throws(new AmazonCloudWatchLogsException(Secret) { ErrorCode = "AccessDeniedException" });
        var provider = new CloudWatchProvider(TestTransport.Resolver(encryption.Object), log.For<CloudWatchProvider>());
        var source = new DataSource { Id = 9, Name = "logs", DataSourceType = DataSourceType.CloudWatch, EncryptedConnectionData = "encrypted" };

        var result = await provider.ExecuteQueryAsync(source, "fields @message", [], CancellationToken.None);

        result.Success.Should().BeFalse();
        log.Entries.Should().ContainSingle(x => x.Level >= LogLevel.Warning)
            .Which.Message.Should().StartWith("Query execution failed for CloudWatch data source 9 with AmazonCloudWatchLogsException (error code AccessDeniedException, at ");
        log.Contains(Secret).Should().BeFalse();
    }

    private static void ThrowWrapped()
    {
        try
        {
            throw new FakeDbException(Secret, "22P02", new IOException(Secret));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(Secret, ex);
        }
    }

    private static async Task ThrowFromAsyncAsync()
    {
        await Task.Yield();
        throw new FakeDbException(Secret, "42P01");
    }

    private static Exception Caught(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new AssertionException("nothing was thrown");
    }

    private static async Task<Exception> CaughtAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new AssertionException("nothing was thrown");
    }

    private static DatabaseProvider FailingProvider(Exception failure, LogRecorder log)
    {
        var resolver = new Mock<IDataSourceConnectionResolver>();
        resolver
            .Setup(x => x.GetConnectionString(It.IsAny<DataSource>()))
            .Throws(failure);

        return new DatabaseProvider(
            resolver.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            new HostDataSourceGuard(new TestHostDataSources()),
            new ConfiguredDatabaseResolver(resolver.Object, NullLogger<ConfiguredDatabaseResolver>.Instance),
            log.For<DatabaseProvider>(),
            TestQueryServices.QueryOptions);
    }

    private static DataSource Source()
    {
        return new DataSource
        {
            Id = 7,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "unused",
            DatabaseEngineType = DatabaseEngineType.MSSQL
        };
    }

    // SqlException has no public constructor: build one the way the driver does, through its internal factory.
    private static SqlException SqlServerError(int number)
    {
        var errorConstructor = typeof(SqlError)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(x => x.GetParameters().Length == 8 && x.GetParameters()[0].ParameterType == typeof(int));
        var error = (SqlError)errorConstructor.Invoke([number, (byte)0, (byte)0, "server", Secret, "procedure", 0, null]);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(errors, [error]);
        var create = typeof(SqlException)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(x => x.Name == "CreateException"
                && x.GetParameters().Length == 2
                && x.GetParameters()[0].ParameterType == typeof(SqlErrorCollection));

        return (SqlException)create.Invoke(null, [errors, "16.0"])!;
    }

    private sealed class FakeDbException(string message, string sqlState, Exception? inner = null) : DbException(message, inner)
    {
        public override string SqlState => sqlState;
    }
}
