using System.Reflection;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.LlmProviders;
using Beacon.Api.Authentication;
using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Queries on hot paths send the SQL they should and no more of it: a once-per-process first-run answer, an API key read
/// that never loads the owner's credentials and writes its last-used time once a minute, and projected reads in place of
/// loading whole object graphs.
/// </summary>
[TestFixture]
public class QueryHotPathTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task FirstRunMiddleware_AsksTheDatabaseOnce_ThenRemembersSetupIsComplete()
    {
        var configuration = new BeaconConfiguration();
        configuration.EnableUserManagement();
        var reached = 0;
        var middleware = new FirstRunSetupMiddleware(
            _ =>
            {
                reached++;
                return Task.CompletedTask;
            },
            configuration,
            "/beacon");
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        for (var i = 0; i < 3; i++)
        {
            var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
            context.Request.Path = "/beacon/api/projects";
            await middleware.InvokeAsync(context, users.Object);
        }

        reached.Should().Be(3);
        users.Verify(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task FirstRunMiddleware_KeepsAskingWhileSetupIsOpen()
    {
        var configuration = new BeaconConfiguration();
        configuration.EnableUserManagement();
        var middleware = new FirstRunSetupMiddleware(_ => Task.CompletedTask, configuration, "/beacon");
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        for (var i = 0; i < 2; i++)
        {
            var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
            context.Request.Path = "/beacon/api/projects";
            await middleware.InvokeAsync(context, users.Object);
        }

        users.Verify(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task ApiKeyValidationQuery_NeverSelectsTheOwnersPasswordColumns_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();

        await BuildApiKeys(capture, Now).ValidateApiKeyAsync("sk-sem_hotpathkey-1");

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().NotContain("password_hash");
        sql.Should().NotContain("password_salt");
        sql.Should().Contain("key_hash");
    }

    [Test]
    public async Task UpdateLastUsed_IsAConditionalUpdate_WrittenOncePerMinutePerKey_Translates()
    {
        var capture = new SqlCapture().ThenRowsAffected(1);
        var time = new FakeTimeProvider(Now);
        var keys = BuildApiKeys(capture, time);

        await keys.UpdateLastUsedAsync(910001);
        await keys.UpdateLastUsedAsync(910001);
        time.Advance(TimeSpan.FromSeconds(30));
        await keys.UpdateLastUsedAsync(910001);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().StartWith("UPDATE beacon.api_key_credentials");
        sql.Should().Contain("last_used_at");
        sql.Should().Contain("last_used_at IS NULL OR");

        time.Advance(TimeSpan.FromSeconds(31));
        capture.ThenRowsAffected(1);
        await keys.UpdateLastUsedAsync(910001);
        capture.Commands.Should().HaveCount(2);
    }

    [Test]
    public async Task UpdateLastUsed_ThrottlesEachKeyOnItsOwn()
    {
        var capture = new SqlCapture().ThenRowsAffected(1).ThenRowsAffected(1);
        var keys = BuildApiKeys(capture, Now);

        await keys.UpdateLastUsedAsync(910002);
        await keys.UpdateLastUsedAsync(910003);

        capture.Commands.Should().HaveCount(2);
    }

    [Test]
    public async Task UpdateLastUsed_ThrottleIsPerServiceInstance_WhenNoneIsShared()
    {
        var capture = new SqlCapture().ThenRowsAffected(1).ThenRowsAffected(1);

        await BuildApiKeys(capture, Now).UpdateLastUsedAsync(910004);
        await BuildApiKeys(capture, Now).UpdateLastUsedAsync(910004);

        capture.Commands.Should().HaveCount(2);
    }

    [Test]
    public async Task ControlTowerLastExecutions_ReadsTheLatestRunOfThePagesSubscriptionsInOneRowNumberPass_Translates()
    {
        var capture = new SqlCapture();
        await using var context = await capture.Factory().CreateDbContextAsync();
        var method = typeof(ControlTowerService).GetMethod("LoadLastExecutions", BindingFlags.NonPublic | BindingFlags.Static)!;

        var call = () => (Task)method.Invoke(null, [context, new List<int> { 1, 2 }, CancellationToken.None])!;
        await call.Should().ThrowAsync<SqlCapturedException>();

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("FROM beacon.query_execution_history");
        sql.Should().Contain("ROW_NUMBER() OVER(PARTITION BY");
        sql.Should().Contain("= ANY (@pageIds)");
    }

    [Test]
    public async Task AiActorQueryContext_ReadsTheLatestRunInTheQuery_WithoutLoadingTheHistory_Translates()
    {
        var capture = new SqlCapture();
        var actors = BuildAiActors(capture);
        var method = typeof(AiActorService).GetMethod("GetQueryContextAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var call = () => (Task)method.Invoke(actors, [1, CancellationToken.None])!;
        await call.Should().ThrowAsync<SqlCapturedException>();

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("FROM beacon.queries");
        sql.Should().Contain("beacon.query_execution_history");
        sql.Should().Contain("LIMIT 1");
    }

    [Test]
    public async Task ApiKeyValidationQuery_NestedOwnerProjection_TranslatesOnSqlServer()
    {
        var capture = SqlCapture.OnSqlServer().ThenNoRows();

        await BuildApiKeys(capture, Now).ValidateApiKeyAsync("sk-sem_hotpathkey-1");

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().NotContain("PasswordHash", because: "the owner's credentials are never selected");
        sql.Should().NotContain("PasswordSalt");
        sql.Should().Contain("KeyHash");
    }

    [Test]
    public async Task ControlTowerLastExecutions_LatestRunPick_TranslatesOnSqlServer()
    {
        var capture = SqlCapture.OnSqlServer();
        await using var context = await capture.Factory().CreateDbContextAsync();
        var method = typeof(ControlTowerService).GetMethod("LoadLastExecutions", BindingFlags.NonPublic | BindingFlags.Static)!;

        var call = () => (Task)method.Invoke(null, [context, new List<int> { 1, 2 }, CancellationToken.None])!;
        await call.Should().ThrowAsync<SqlCapturedException>();

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("ROW_NUMBER() OVER(PARTITION BY");
    }

    [Test]
    public async Task AiActorQueryContext_CorrelatedLatestRun_TranslatesOnSqlServer()
    {
        var capture = SqlCapture.OnSqlServer();
        var actors = BuildAiActors(capture);
        var method = typeof(AiActorService).GetMethod("GetQueryContextAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var call = () => (Task)method.Invoke(actors, [1, CancellationToken.None])!;
        await call.Should().ThrowAsync<SqlCapturedException>();

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("FROM [beacon].[Queries]");
        sql.Should().Contain("[beacon].[QueryExecutionHistory]");
        sql.Should().Contain("ROW_NUMBER() OVER(PARTITION BY [q1].[SubscriptionId] ORDER BY [q1].[CreatedTime] DESC, [q1].[Id] DESC)");
    }

    private static AiActorService BuildAiActors(SqlCapture capture) =>
        new AiActorService(
            capture.Factory(),
            Mock.Of<ILlmProvider>(),
            Mock.Of<IDatabaseMetadataService>(),
            Mock.Of<IQueryService>(),
            Mock.Of<ISubscriptionService>(),
            Mock.Of<Beacon.Core.Worker.IBeaconScheduler>(),
            Mock.Of<Beacon.Core.Authorization.IRunAsScope>(),
            Mock.Of<Beacon.Core.Authorization.IExecutionAccessCheck>(),
            Mock.Of<IStepSqlSaveCheck>(),
            Mock.Of<IQueryProposalCheck>(),
            Mock.Of<IQueryVersionService>(),
            new BeaconConfiguration(),
            new RecordingSecurityAudit(),
            NullLogger<AiActorService>.Instance);

    private static ApiKeyService BuildApiKeys(SqlCapture capture, DateTimeOffset now) =>
        BuildApiKeys(capture, new FakeTimeProvider(now));

    private static ApiKeyService BuildApiKeys(SqlCapture capture, TimeProvider time) =>
        new(
            capture.Factory(),
            Options.Create(new ApiKeyOptions()),
            time,
            new RecordingSecurityAudit(),
            TestProjectAccess.Actors(),
            NullLogger<ApiKeyService>.Instance);
}
