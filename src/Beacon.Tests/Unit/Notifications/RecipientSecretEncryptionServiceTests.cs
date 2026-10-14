using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Notifications;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// The one-off re-encryption of recipient secrets stored before encryption at rest. Runs on the real Npgsql and SQL
/// Server providers with the connection suppressed and every command captured, so the translated SQL itself is checked:
/// candidates include archived recipients, each write is a compare-and-swap on exactly the values it read (so a
/// concurrent edit wins), and a recipient that fails is logged and skipped before the run reports the failure.
/// </summary>
[TestFixture]
public class RecipientSecretEncryptionServiceTests
{
    private const string PlainDestination = "https://hooks.slack.com/services/T0/B0/PATHSECRET";
    private const string PlainHeaders = "{\"Authorization\":\"Bearer s3cr3t\"}";

    [Test]
    public async Task Candidates_AreThePlaintextRows_ArchivedIncluded()
    {
        var database = new CommandCapture();

        var result = await Service(database).EncryptStoredSecretsAsync(CancellationToken.None);

        result.Should().Be(new RecipientSecretEncryptionResult(0, 0, 0, 0));
        var select = database.Commands.Should().ContainSingle().Subject.Text;
        select.Should().Contain("r.destination NOT LIKE 'enc:%'").And.Contain("r.headers_json NOT LIKE 'enc:%'");
        select.Should().NotContain("archived_time", "archived recipients hold secrets too");
    }

    [Test]
    public async Task PlaintextDestinationWithoutHeaders_IsEncryptedConditionalOnTheValuesRead()
    {
        var database = new CommandCapture { Rows = [[1, PlainDestination, null]] };

        var result = await Service(database).EncryptStoredSecretsAsync(CancellationToken.None);

        result.Should().Be(new RecipientSecretEncryptionResult(1, 1, 0, 0));
        var (set, where, parameters) = Update(database, 1);
        set.Should().Contain("destination = @").And.Contain("headers_json = NULL");
        where.Should().Contain("r.id = @").And.Contain("r.destination = @").And.Contain("r.headers_json IS NULL");
        parameters.Should().Contain(PlainDestination, "the write is conditional on the value read");
        Protector().Unprotect(parameters.OfType<string>().Single(x => x.StartsWith(RecipientSecretProtector.EncryptedPrefix)))
            .Should().Be(PlainDestination);
    }

    [Test]
    public async Task PlaintextHeaders_AreEncryptedAndCompared()
    {
        var encryptedDestination = Protector().Protect("https://hooks.example.com/b");
        var database = new CommandCapture { Rows = [[2, encryptedDestination, PlainHeaders]] };

        await Service(database).EncryptStoredSecretsAsync(CancellationToken.None);

        var (_, where, parameters) = Update(database, 1);
        where.Should().Contain("r.headers_json = @");
        var strings = parameters.OfType<string>().ToList();
        strings.Should().Contain(PlainHeaders).And.Contain(encryptedDestination, "an encrypted destination is kept as is");
        Protector().Unprotect(strings.Single(x => x.StartsWith(RecipientSecretProtector.EncryptedPrefix) && x != encryptedDestination))
            .Should().Be(PlainHeaders);
    }

    [Test]
    public async Task PlaintextDestinationWithEncryptedHeaders_KeepsTheHeaders()
    {
        var encryptedHeaders = Protector().Protect(PlainHeaders);
        var database = new CommandCapture { Rows = [[3, PlainDestination, encryptedHeaders]] };

        await Service(database).EncryptStoredSecretsAsync(CancellationToken.None);

        var (_, _, parameters) = Update(database, 1);
        parameters.OfType<string>().Where(x => x == encryptedHeaders).Should().HaveCount(2, "the headers are both kept and compared unchanged");
    }

    [Test]
    public async Task EmptyHeaders_AreLeftEmpty()
    {
        var database = new CommandCapture { Rows = [[4, PlainDestination, string.Empty]] };

        await Service(database).EncryptStoredSecretsAsync(CancellationToken.None);

        var (_, where, parameters) = Update(database, 1);
        where.Should().Contain("r.headers_json = @");
        parameters.Where(x => x is "").Should().HaveCount(2);
    }

    [Test]
    public async Task ARowEditedMeanwhile_IsCountedAsSkipped()
    {
        var database = new CommandCapture { Rows = [[1, PlainDestination, null]], RowsAffected = 0 };

        var result = await Service(database).EncryptStoredSecretsAsync(CancellationToken.None);

        result.Should().Be(new RecipientSecretEncryptionResult(1, 0, 1, 0));
    }

    [Test]
    public async Task AFailingRow_IsLoggedAndSkipped_ThenTheRunFails()
    {
        var database = new CommandCapture
        {
            Rows = [[1, PlainDestination, null], [2, PlainDestination, null], [3, PlainDestination, null]],
            FailingWrite = 2,
        };
        var logger = new CapturingLogger<RecipientSecretEncryptionService>();

        var act = () => Service(database, logger).EncryptStoredSecretsAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("1 notification recipients could not be encrypted*");
        database.Writes.Should().Be(3, "the rows after the failing one are still encrypted");
        logger.Entries.Should().Contain(x => x.Level == LogLevel.Error && x.Message.Contains("recipient 2") && x.Message.Contains(nameof(InvalidOperationException)));
        logger.Entries.Should().Contain(x => x.Level == LogLevel.Information && x.Message.Contains("3 with plaintext secrets, 2 encrypted, 0 changed meanwhile, 1 failed"));
        logger.TextAtOrAbove(LogLevel.Trace).Should().NotContain("PATHSECRET");
    }

    [Test]
    public async Task Cancellation_StopsTheRun()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var database = new CommandCapture { Rows = [[1, PlainDestination, null]] };

        var act = () => Service(database).EncryptStoredSecretsAsync(cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        database.Writes.Should().Be(0);
    }

    [Test]
    public async Task TranslatesOnSqlServer()
    {
        var database = new CommandCapture { Rows = [[1, PlainDestination, PlainHeaders]] };
        var service = new RecipientSecretEncryptionService(SqlServerFactory(database), Protector(), new CapturingLogger<RecipientSecretEncryptionService>());

        var result = await service.EncryptStoredSecretsAsync(CancellationToken.None);

        result.Encrypted.Should().Be(1);
        database.Commands[0].Text.Should().Contain("[r].[Destination] NOT LIKE N'enc:%'").And.NotContain("ArchivedTime");
        var (set, where, parameters) = Update(database, 1);
        set.Should().Contain("[Destination] = @").And.Contain("[HeadersJson] = @");
        where.Should().Contain("[r].[Id] = @").And.Contain("[r].[Destination] = @").And.Contain("[r].[HeadersJson] = @");
        parameters.OfType<string>().Where(x => x.StartsWith(RecipientSecretProtector.EncryptedPrefix))
            .Select(Protector().Unprotect)
            .Should().BeEquivalentTo([PlainDestination, PlainHeaders]);
    }

    private static RecipientSecretEncryptionService Service(CommandCapture database, ILogger<RecipientSecretEncryptionService>? logger = null)
    {
        return new RecipientSecretEncryptionService(database.NpgsqlFactory(), Protector(), logger ?? new CapturingLogger<RecipientSecretEncryptionService>());
    }

    private static RecipientSecretProtector Protector()
    {
        return NotificationTestKit.Protector();
    }

    // The UPDATE at position `index` (after the candidate SELECT), split into its SET and WHERE parts.
    private static (string Set, string Where, List<object?> Parameters) Update(CommandCapture database, int index)
    {
        var (text, parameters) = database.Commands[index];
        text.Should().StartWith("UPDATE");
        var whereAt = text.IndexOf("WHERE", StringComparison.Ordinal);
        var setAt = text.IndexOf("SET", StringComparison.Ordinal);

        return (text[setAt..whereAt], text[whereAt..], parameters);
    }

    private static IDbContextFactory<BeaconContext> SqlServerFactory(CommandCapture database)
    {
        var options = new DbContextOptionsBuilder<SqlServerTestContext>()
            .UseSqlServer("Server=localhost;Database=unused;Trusted_Connection=True;TrustServerCertificate=True")
            .AddInterceptors(database)
            .Options;

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SqlServerTestContext(options));

        return factory.Object;
    }

    private sealed class SqlServerTestContext(DbContextOptions<SqlServerTestContext> options) : BeaconContext(options, "beacon");
}
