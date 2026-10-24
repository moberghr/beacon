using Beacon.Core.ChangeApprovals;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// The LLM connection lock in Beacon's own database: an exclusive application lock owned by the transaction that takes it
/// (released when it commits or rolls back), on a fixed key passed as a parameter, never outside a transaction.
/// </summary>
[TestFixture]
public class LlmConnectionLockTests
{
    [Test]
    public async Task OnPostgreSql_ItIsATransactionAdvisoryLockOnAFixedKey()
    {
        var capture = new SqlCapture().ThenRowsAffected(-1);

        await AcquireInATransactionAsync(capture);

        capture.Commands.Should().ContainSingle().Which.Should().Be("SELECT pg_advisory_xact_lock(@p0)");
        capture.CommandParameters.Single().Should().Equal(new Dictionary<string, object?> { ["@p0"] = DatabaseLlmConnectionLock.PostgreSqlLockKey });
    }

    [Test]
    public async Task OnSqlServer_ItIsAnExclusiveAppLockOwnedByTheTransaction_AndFailsWhenNotGranted()
    {
        var capture = SqlCapture.OnSqlServer().ThenRowsAffected(-1);

        await AcquireInATransactionAsync(capture);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("EXEC @lockResult = sp_getapplock @Resource = @p0, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @p1;")
            .And.Contain("IF @lockResult < 0 THROW");
        capture.CommandParameters.Single().Should().Equal(new Dictionary<string, object?>
        {
            ["@p0"] = DatabaseLlmConnectionLock.SqlServerResource,
            ["@p1"] = DatabaseLlmConnectionLock.SqlServerLockTimeoutMilliseconds
        });
    }

    [Test]
    public async Task OutsideATransaction_ItIsRefused_AndNothingIsSent()
    {
        var capture = new SqlCapture();
        await using var context = await capture.Factory().CreateDbContextAsync(CancellationToken.None);

        var act = () => new DatabaseLlmConnectionLock().AcquireAsync(context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        capture.Commands.Should().BeEmpty();
    }

    [Test]
    public void OnAnotherProvider_ItIsRefused()
    {
        var act = () => DatabaseLlmConnectionLock.Statement("Microsoft.EntityFrameworkCore.Sqlite");

        act.Should().Throw<InvalidOperationException>();
    }

    private static async Task AcquireInATransactionAsync(SqlCapture capture)
    {
        await using var context = await capture.Factory().CreateDbContextAsync(CancellationToken.None);
        await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);

        await new DatabaseLlmConnectionLock().AcquireAsync(context, CancellationToken.None);
    }
}
