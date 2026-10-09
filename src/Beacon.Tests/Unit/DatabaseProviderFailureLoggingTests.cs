using System.Data.Common;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A failed query is logged by exception type and driver error code only (§1.11): a server or conversion error message
/// can quote row values, so neither the message nor the exception object reaches the log. The caller of an ordinary
/// data source still receives the message.
/// </summary>
[TestFixture]
public class DatabaseProviderFailureLoggingTests
{
    private const string SensitiveValue = "0101302989";

    [Test]
    public async Task ExecuteQueryAsync_ServerErrorQuotingAValue_LogsTheTypeAndCodeOnly()
    {
        var logger = new RecordingLogger();
        var provider = CreateProvider(new FakeDbException($"invalid input syntax for type integer: \"{SensitiveValue}\"", "22P02"), logger);

        var result = await provider.ExecuteQueryAsync(OrdinarySource(), "SELECT 1", [], CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain(SensitiveValue, "an ordinary source's caller still gets the server message");
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Exception.Should().BeNull("the exception carries the message");
        entry.Message.Should().NotContain(SensitiveValue);
        entry.Message.Should().Be("Query execution failed for database data source 7 with FakeDbException (error code 22P02)");
    }

    [Test]
    public async Task ValidateQueryAsync_WrappedServerError_LogsTheInnerCodeOnly()
    {
        var logger = new RecordingLogger();
        var wrapped = new InvalidOperationException($"failed near '{SensitiveValue}'", new FakeDbException($"value '{SensitiveValue}' is out of range", "22003"));
        var provider = CreateProvider(wrapped, logger);

        var result = await provider.ValidateQueryAsync(OrdinarySource(), "SELECT 1 AS passed", CancellationToken.None);

        result.IsValid.Should().BeFalse();
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Exception.Should().BeNull();
        entry.Message.Should().Be("Query validation failed for database data source 7 with InvalidOperationException (error code 22003)");
    }

    [Test]
    public async Task ExecuteQueryAsync_ErrorWithoutADriverCode_LogsNone()
    {
        var logger = new RecordingLogger();
        var provider = CreateProvider(new InvalidOperationException($"conversion failed for value '{SensitiveValue}'"), logger);

        await provider.ExecuteQueryAsync(OrdinarySource(), "SELECT 1", [], CancellationToken.None);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Exception.Should().BeNull();
        entry.Message.Should().Be("Query execution failed for database data source 7 with InvalidOperationException (error code none)");
    }

    private static DatabaseProvider CreateProvider(Exception failure, RecordingLogger logger)
    {
        var resolver = new Mock<IDataSourceConnectionResolver>();
        resolver
            .Setup(x => x.GetConnectionString(It.IsAny<DataSource>()))
            .Throws(failure);

        return new DatabaseProvider(
            resolver.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            new HostDataSourceGuard(new NoHostSources()),
            logger);
    }

    private static DataSource OrdinarySource()
    {
        return new DataSource
        {
            Id = 7,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "unused",
            DatabaseEngineType = DatabaseEngineType.PostgreSQL
        };
    }

    private sealed class NoHostSources : IHostDataSourceRegistry
    {
        public IReadOnlyList<HostDataSourceRegistration> Registrations => [];

        public HostExposureSnapshot? GetSnapshot(string hostManagedKey) => null;
    }

    private sealed class FakeDbException(string message, string sqlState) : DbException(message)
    {
        public override string SqlState => sqlState;
    }

    private sealed class RecordingLogger : ILogger<DatabaseProvider>
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((formatter(state, exception), exception));
        }
    }
}
