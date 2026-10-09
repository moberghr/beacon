using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Handlers.Setup;
using Beacon.Core.Models;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// First-run setup: the initial super admin is created only with the setup token (configured, or generated and written
/// once to the console — never the log — while setup is open), compared in constant time; setup endpoints are throttled
/// in their own partition; a request that loses a concurrent first-run race is told setup is complete (or to retry)
/// rather than seeing a server error.
/// </summary>
[TestFixture]
public class FirstRunSetupTests
{
    // Exactly the 32-character minimum.
    private const string ConfiguredToken = "configured-setup-token-for-tests";
    private const string StrongPassword = "Aa1!aaaa";

    [TestCase(ConfiguredToken, ConfiguredToken, true)]
    [TestCase("configured-setup-token-for-tesTs", ConfiguredToken, false)]
    [TestCase("short", ConfiguredToken, false)]
    [TestCase(ConfiguredToken + "-and-more", ConfiguredToken, false)]
    [TestCase("", ConfiguredToken, false)]
    [TestCase(null, ConfiguredToken, false)]
    [TestCase("anything", "", false)]
    public void FixedTimeSecretComparer_MatchesOnlyTheExactSecret(string? presented, string expected, bool matches)
    {
        FixedTimeSecretComparer.Matches(presented, expected).Should().Be(matches);
    }

    [Test]
    public void ConfiguredToken_IsTheOnlyAcceptedToken_AndIsNeverWrittenAnywhere()
    {
        var logs = new LogRecorder();
        var console = new SignalingWriter();
        var token = new FirstRunSetupToken(Configuration(ConfiguredToken), logs.For<FirstRunSetupToken>(), console);

        token.AnnounceWhileFirstRun();

        token.IsGenerated.Should().BeFalse();
        token.Verify(ConfiguredToken).Should().BeTrue();
        token.Verify($"  {ConfiguredToken}\n").Should().BeTrue("surrounding whitespace from a copy-paste is ignored");
        token.Verify("wrong").Should().BeFalse();
        token.Verify(null).Should().BeFalse();
        console.ToString().Should().BeEmpty();
        logs.Contains(ConfiguredToken).Should().BeFalse();
    }

    [Test]
    public void GeneratedToken_IsWrittenOnceToTheConsole_AndTheLogOnlySaysWhereItWent()
    {
        var logs = new LogRecorder();
        var console = new SignalingWriter();
        var token = new FirstRunSetupToken(Configuration(setupToken: null), logs.For<FirstRunSetupToken>(), console);

        token.AnnounceWhileFirstRun();
        token.AnnounceWhileFirstRun();

        token.IsGenerated.Should().BeTrue();
        var written = TokenFrom(console);
        written.Should().MatchRegex("^[0-9a-f]{64}$", "a 256-bit random token");
        Occurrences(console.ToString(), written).Should().Be(1);
        token.Verify(written).Should().BeTrue();
        token.Verify(written.ToUpperInvariant()).Should().BeFalse();
        var entry = logs.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("console");
        logs.Contains(written).Should().BeFalse("the token never goes through ILogger");
    }

    [Test]
    public void GeneratedToken_DiffersPerProcess()
    {
        var first = new FirstRunSetupToken(Configuration(setupToken: null), NullLogger<FirstRunSetupToken>.Instance, TextWriter.Null);
        var console = new SignalingWriter();
        var second = new FirstRunSetupToken(Configuration(setupToken: null), NullLogger<FirstRunSetupToken>.Instance, console);

        second.AnnounceWhileFirstRun();

        first.Verify(TokenFrom(console)).Should().BeFalse();
    }

    [Test]
    public void Verify_GoesThroughTheFixedTimeComparer()
    {
        var token = new FirstRunSetupToken(Configuration(ConfiguredToken), NullLogger<FirstRunSetupToken>.Instance);
        token.Comparer.Should().Be(
            (Func<string?, string, bool>)FixedTimeSecretComparer.Matches,
            "the default comparison is the constant-time one");

        var calls = new List<(string? Presented, string Expected)>();
        var spied = new FirstRunSetupToken(Configuration(ConfiguredToken), NullLogger<FirstRunSetupToken>.Instance)
        {
            Comparer = (presented, expected) =>
            {
                calls.Add((presented, expected));
                return FixedTimeSecretComparer.Matches(presented, expected);
            }
        };

        spied.Verify($" {ConfiguredToken} ").Should().BeTrue();
        calls.Should().Equal((ConfiguredToken, ConfiguredToken));
    }

    [Test]
    public async Task Handler_AfterSetup_RefusesWithoutCreatingAnything()
    {
        var users = new Mock<IUserManagementService>();
        users.Setup(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateHandler(users).Handle(Command(ConfiguredToken), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Setup has already been completed.");
        users.Verify(x => x.CreateSuperAdminAsync(It.IsAny<CreateSuperAdminRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-the-setup-token")]
    public async Task Handler_WithoutTheSetupToken_RefusesWithoutCreatingAnything_AndLogsNoToken(string? presented)
    {
        var users = FirstRunUsers();
        var logs = new LogRecorder();

        var result = await CreateHandler(users, logs).Handle(Command(presented), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.TokenRejected.Should().BeTrue();
        result.Failed.Should().BeFalse();
        users.Verify(x => x.CreateSuperAdminAsync(It.IsAny<CreateSuperAdminRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        logs.Entries.Should().Contain(x => x.Level == LogLevel.Warning && x.Message.Contains("setup token is missing or invalid"));
        logs.Contains(ConfiguredToken).Should().BeFalse();
        if (!string.IsNullOrEmpty(presented))
        {
            logs.Contains(presented).Should().BeFalse("the presented value is never logged either");
        }
    }

    [Test]
    public async Task Handler_WithTheSetupToken_CreatesTheSuperAdmin_AndLogsItsId()
    {
        var users = FirstRunUsers();
        users
            .Setup(x => x.CreateSuperAdminAsync(It.IsAny<CreateSuperAdminRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconUserData { Id = 9, ExternalId = "x", UserName = "admin", IsSuperAdmin = true });
        var logs = new LogRecorder();

        var result = await CreateHandler(users, logs).Handle(Command(ConfiguredToken), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.UserId.Should().Be(9);
        logs.Entries.Should().Contain(x => x.Level == LogLevel.Warning && x.Message.Contains("user id 9"));
        logs.Contains(ConfiguredToken).Should().BeFalse();
        logs.Contains(StrongPassword).Should().BeFalse();
    }

    [Test]
    public async Task Handler_WeakPassword_IsARefusalWithItsReason_NotAServerError()
    {
        var users = FirstRunUsers();
        users
            .Setup(x => x.CreateSuperAdminAsync(It.IsAny<CreateSuperAdminRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BeaconException("Password must be at least 12 characters long."));

        var result = await CreateHandler(users).Handle(Command(ConfiguredToken), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Failed.Should().BeFalse();
        result.Conflict.Should().BeFalse();
        result.Error.Should().Be("Password must be at least 12 characters long.");
    }

    [TestCaseSource(nameof(SerializationConflicts))]
    public async Task Handler_SerializationConflict_AfterTheWinnerCommitted_IsToldSetupIsComplete(Exception conflict)
    {
        var users = new Mock<IUserManagementService>();
        users
            .SetupSequence(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);
        users
            .Setup(x => x.CreateSuperAdminAsync(It.IsAny<CreateSuperAdminRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(conflict);

        var result = await CreateHandler(users).Handle(Command(ConfiguredToken), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Failed.Should().BeFalse();
        result.Conflict.Should().BeFalse();
        result.Error.Should().Be("Setup has already been completed.");
    }

    [Test]
    public async Task Handler_SerializationConflict_WhileTheWinnerIsStillRunning_AsksToRetry()
    {
        var users = FirstRunUsers();
        users
            .Setup(x => x.CreateSuperAdminAsync(It.IsAny<CreateSuperAdminRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(PostgresError("40001"));
        var logs = new LogRecorder();

        var result = await CreateHandler(users, logs).Handle(Command(ConfiguredToken), CancellationToken.None);

        result.Conflict.Should().BeTrue();
        result.Failed.Should().BeFalse();
        logs.Entries.Should().Contain(x => x.Level == LogLevel.Warning && x.Exception is PostgresException);
    }

    [Test]
    public async Task Handler_LosingARaceAnotherWay_IsToldSetupIsComplete()
    {
        var users = new Mock<IUserManagementService>();
        users
            .SetupSequence(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);
        users
            .Setup(x => x.CreateSuperAdminAsync(It.IsAny<CreateSuperAdminRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("duplicate", PostgresError("23505")));

        var result = await CreateHandler(users).Handle(Command(ConfiguredToken), CancellationToken.None);

        result.Failed.Should().BeFalse();
        result.Error.Should().Be("Setup has already been completed.");
    }

    [Test]
    public async Task Handler_UnexpectedFailureWhileStillFirstRun_IsAServerError_AndLogsTheCauseFirst()
    {
        var cause = new InvalidOperationException("database unavailable");
        var users = FirstRunUsers();
        users
            .Setup(x => x.CreateSuperAdminAsync(It.IsAny<CreateSuperAdminRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(cause);
        var logs = new LogRecorder();

        var result = await CreateHandler(users, logs).Handle(Command(ConfiguredToken), CancellationToken.None);

        result.Failed.Should().BeTrue();
        logs.Entries
            .Where(x => x.Level == LogLevel.Error)
            .Select(x => x.Exception)
            .First()
            .Should().BeSameAs(cause, "the original failure is logged before anything else is tried");
    }

    [Test]
    public async Task Handler_FailureWhenTheStateCannotBeReRead_IsAServerError_AndLogsBothFailures()
    {
        var cause = new InvalidOperationException("database unavailable");
        var recheck = new TimeoutException("still unavailable");
        var users = new Mock<IUserManagementService>();
        users
            .SetupSequence(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ThrowsAsync(recheck);
        users
            .Setup(x => x.CreateSuperAdminAsync(It.IsAny<CreateSuperAdminRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(cause);
        var logs = new LogRecorder();

        var result = await CreateHandler(users, logs).Handle(Command(ConfiguredToken), CancellationToken.None);

        result.Failed.Should().BeTrue();
        logs.Entries
            .Where(x => x.Level == LogLevel.Error)
            .Select(x => x.Exception)
            .Should().Equal(cause, recheck);
    }

    [Test]
    public async Task Handler_CancelledRequest_IsCancelled_NotReportedAsAFailure()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var users = FirstRunUsers();
        users
            .Setup(x => x.CreateSuperAdminAsync(It.IsAny<CreateSuperAdminRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancelled.Token));

        var act = () => CreateHandler(users).Handle(Command(ConfiguredToken), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public void SerializationConflict_IsRecognisedOnEveryProvider_AndNothingElseIs()
    {
        foreach (var conflict in SerializationConflicts())
        {
            DbSerializationConflict.IsSerializationConflict((Exception)conflict.Arguments[0]!).Should().BeTrue();
        }

        DbSerializationConflict.IsSerializationConflict(PostgresError("23505")).Should().BeFalse();
        DbSerializationConflict.IsSerializationConflict(SqlServerError(2627)).Should().BeFalse();
        DbSerializationConflict.IsSerializationConflict(new InvalidOperationException("x")).Should().BeFalse();
        DbSerializationConflict.IsSerializationConflict(null).Should().BeFalse();
    }

    [Test]
    public async Task Endpoint_WrongToken_Is403_AndCreatesNothing()
    {
        var users = FirstRunUsers();
        await using var app = await StartSetupHostAsync(users.Object);

        var response = await app.GetTestClient().PostAsJsonAsync("/beacon/api/setup/superadmin", SetupBody("wrong"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        users.Verify(x => x.CreateSuperAdminAsync(It.IsAny<CreateSuperAdminRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Endpoint_RightToken_Is200_AndCreatesTheSuperAdmin()
    {
        var users = FirstRunUsers();
        users
            .Setup(x => x.CreateSuperAdminAsync(It.IsAny<CreateSuperAdminRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconUserData { Id = 3, ExternalId = "x", UserName = "admin", IsSuperAdmin = true });
        await using var app = await StartSetupHostAsync(users.Object);

        var response = await app.GetTestClient().PostAsJsonAsync("/beacon/api/setup/superadmin", SetupBody(ConfiguredToken));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        users.Verify(
            x => x.CreateSuperAdminAsync(It.Is<CreateSuperAdminRequest>(y => y.UserName == "admin"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Endpoint_WeakPassword_Is400_WithTheReason()
    {
        var users = FirstRunUsers();
        users
            .Setup(x => x.CreateSuperAdminAsync(It.IsAny<CreateSuperAdminRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BeaconException("Password must be at least 12 characters long."));
        await using var app = await StartSetupHostAsync(users.Object);

        var response = await app.GetTestClient().PostAsJsonAsync("/beacon/api/setup/superadmin", SetupBody(ConfiguredToken));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Password must be at least 12 characters long.");
    }

    [Test]
    public async Task Endpoint_ConcurrentSetupStillRunning_Is409()
    {
        var users = FirstRunUsers();
        users
            .Setup(x => x.CreateSuperAdminAsync(It.IsAny<CreateSuperAdminRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(PostgresError("40001"));
        await using var app = await StartSetupHostAsync(users.Object);

        var response = await app.GetTestClient().PostAsJsonAsync("/beacon/api/setup/superadmin", SetupBody(ConfiguredToken));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [TestCase("GET", "/beacon/api/setup/status")]
    [TestCase("GET", "/beacon/api/setup/roles")]
    [TestCase("POST", "/beacon/api/setup/superadmin")]
    public async Task Endpoints_AreThrottledPerRemoteAddress(string method, string path)
    {
        await using var app = await StartSetupHostAsync(FirstRunUsers().Object);
        var client = app.GetTestClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            var response = method == "GET"
                ? await client.GetAsync(path)
                : await client.PostAsJsonAsync(path, SetupBody("wrong"));
            statuses.Add(response.StatusCode);
        }

        statuses.Take(10).Should().NotContain(HttpStatusCode.TooManyRequests);
        statuses.Last().Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task SetupThrottle_DoesNotSpendTheLoginBudget()
    {
        await using var app = await StartSetupHostAsync(FirstRunUsers().Object, mapLoginProbe: true);
        var client = app.GetTestClient();

        for (var i = 0; i < 11; i++)
        {
            await client.GetAsync("/beacon/api/setup/status");
        }

        (await client.GetAsync("/beacon/api/setup/status")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await client.PostAsync("/login-probe", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task LoginThrottle_DoesNotSpendTheSetupBudget()
    {
        await using var app = await StartSetupHostAsync(FirstRunUsers().Object, mapLoginProbe: true);
        var client = app.GetTestClient();

        for (var i = 0; i < 11; i++)
        {
            await client.PostAsync("/login-probe", null);
        }

        (await client.PostAsync("/login-probe", null)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await client.GetAsync("/beacon/api/setup/status")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task GeneratedToken_IsWrittenExactlyOnce_AndNeverLogged_AcrossStartupStatusChecksAndARejectedAttempt()
    {
        var logs = new LogRecorder();
        var console = new SignalingWriter();
        var users = FirstRunUsers();
        await using var app = await StartSetupHostAsync(users.Object, logs, setupToken: null, console: console);
        var token = await console.FirstLine.WaitAsync(TimeSpan.FromSeconds(10));
        var client = app.GetTestClient();

        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync("/beacon/api/setup/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var rejected = await client.PostAsJsonAsync("/beacon/api/setup/superadmin", SetupBody("not-the-token"));

        rejected.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var written = token[(token.LastIndexOf(' ') + 1)..];
        Occurrences(console.ToString(), written).Should().Be(1);
        logs.Entries.Should().NotBeEmpty();
        logs.Contains(written).Should().BeFalse("no log entry, at any level or category, carries the token");
        logs.Contains("not-the-token").Should().BeFalse();
    }

    [Test]
    public async Task Startup_AfterSetup_NeverWritesAToken()
    {
        var logs = new LogRecorder();
        var console = new SignalingWriter();
        var checkedTwice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false)
            .Callback(() =>
            {
                if (Interlocked.Increment(ref checks) == 2)
                {
                    checkedTwice.TrySetResult();
                }
            });

        await using var app = await StartSetupHostAsync(users.Object, logs, setupToken: null, console: console);
        await app.GetTestClient().GetAsync("/beacon/api/setup/status");

        // Both the startup check (off the startup thread) and the status call have looked.
        await checkedTwice.Task.WaitAsync(TimeSpan.FromSeconds(10));
        console.ToString().Should().BeEmpty();
        logs.Contains("setup token").Should().BeFalse();
    }

    [Test]
    public async Task Startup_CheckFailure_LeavesTheAnnouncementToTheStatusEndpoint()
    {
        var logs = new LogRecorder();
        var console = new SignalingWriter();
        var startupFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        logs.Logged += x =>
        {
            if (x.Message.StartsWith("Could not check the first-run state at startup", StringComparison.Ordinal))
            {
                startupFailed.TrySetResult();
            }
        };
        var users = new Mock<IUserManagementService>();
        users
            .SetupSequence(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("database not ready"))
            .ReturnsAsync(true);

        await using var app = await StartSetupHostAsync(users.Object, logs, setupToken: null, console: console);
        await startupFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        console.ToString().Should().BeEmpty();

        await app.GetTestClient().GetAsync("/beacon/api/setup/status");

        TokenFrom(console).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Test]
    public async Task Handler_AnnouncesTheToken_WhenNothingElseHas()
    {
        var console = new SignalingWriter();
        var setupToken = new FirstRunSetupToken(Configuration(setupToken: null), NullLogger<FirstRunSetupToken>.Instance, console);
        var handler = new CreateSuperAdminHandler(FirstRunUsers().Object, setupToken, NullLogger<CreateSuperAdminHandler>.Instance);

        await handler.Handle(Command("wrong"), CancellationToken.None);
        await handler.Handle(Command("wrong"), CancellationToken.None);

        Occurrences(console.ToString(), TokenFrom(console)).Should().Be(1);
    }

    [Test]
    public async Task FirstRunMiddleware_AnnouncesTheToken_WhileRedirectingToSetup()
    {
        var console = new SignalingWriter();
        var services = new ServiceCollection();
        services.AddSingleton(
            new FirstRunSetupToken(Configuration(setupToken: null), NullLogger<FirstRunSetupToken>.Instance, console));
        var configuration = new BeaconConfiguration();
        configuration.EnableUserManagement();
        var middleware = new FirstRunSetupMiddleware(_ => Task.CompletedTask, configuration, "/beacon");
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Path = "/projects";

        await middleware.InvokeAsync(context, FirstRunUsers().Object);

        context.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        context.Response.Headers.Location.ToString().Should().Be("/beacon/setup");
        TokenFrom(console).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Test]
    public async Task Startup_Check_IsBoundToTheHostLifetime()
    {
        var seen = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(x => seen.TrySetResult(x))
            .ReturnsAsync(false);

        var app = await StartSetupHostAsync(users.Object);
        var startupToken = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        startupToken.IsCancellationRequested.Should().BeFalse();

        await app.StopAsync();
        await app.DisposeAsync();

        startupToken.IsCancellationRequested.Should().BeTrue("stopping the host cancels the startup check");
    }

    [Test]
    public async Task Startup_WithoutUserManagement_SaysSetupCannotRun()
    {
        var logs = new LogRecorder();
        var warned = new TaskCompletionSource<RecordedLog>(TaskCreationOptions.RunContinuationsAsynchronously);
        logs.Logged += x =>
        {
            if (x.Message.Contains("user management is not enabled", StringComparison.Ordinal))
            {
                warned.TrySetResult(x);
            }
        };

        await using var app = await StartSetupHostAsync(users: null, logs);

        var entry = await warned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeNull();
    }

    [Test]
    public async Task Roles_AreAnonymousOnlyWhileSetupIsOpen()
    {
        var open = FirstRunUsers();
        await using (var app = await StartSetupHostAsync(open.Object))
        {
            (await app.GetTestClient().GetAsync("/beacon/api/setup/roles")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var done = new Mock<IUserManagementService>();
        done.Setup(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await using (var app = await StartSetupHostAsync(done.Object))
        {
            (await app.GetTestClient().GetAsync("/beacon/api/setup/roles")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public void AddBeaconServices_ReadsTheSetupTokenFromConfiguration()
    {
        using var provider = BuildBeaconServices(ConfiguredToken);
        var token = provider.GetRequiredService<FirstRunSetupToken>();

        token.IsGenerated.Should().BeFalse();
        token.Verify(ConfiguredToken).Should().BeTrue();
    }

    [Test]
    public void AddBeaconServices_ConfiguredTokenShorterThan32Characters_FailsStartup()
    {
        var act = () => BuildBeaconServices(ConfiguredToken[..31]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least 32 characters*");
    }

    private static IEnumerable<TestCaseData> SerializationConflicts()
    {
        yield return new TestCaseData(PostgresError("40001")).SetArgDisplayNames("postgres-serialization-failure");
        yield return new TestCaseData(PostgresError("40P01")).SetArgDisplayNames("postgres-deadlock");
        yield return new TestCaseData(new DbUpdateException("save failed", PostgresError("40001"))).SetArgDisplayNames("postgres-wrapped");
        yield return new TestCaseData(SqlServerError(1205)).SetArgDisplayNames("sqlserver-deadlock-victim");
        yield return new TestCaseData(SqlServerError(3960)).SetArgDisplayNames("sqlserver-snapshot-conflict");
    }

    private static PostgresException PostgresError(string sqlState) => new("conflict", "ERROR", "ERROR", sqlState);

    // SqlException has no public constructor: build one the way the driver does, through its internal factory.
    private static SqlException SqlServerError(int number)
    {
        var errorConstructor = typeof(SqlError)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(x => x.GetParameters().Length == 8 && x.GetParameters()[0].ParameterType == typeof(int));
        var error = (SqlError)errorConstructor.Invoke([number, (byte)0, (byte)0, "server", "conflict", "procedure", 0, null]);
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

    private static Mock<IUserManagementService> FirstRunUsers()
    {
        var users = new Mock<IUserManagementService>();
        users.Setup(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        return users;
    }

    private static CreateSuperAdminHandler CreateHandler(Mock<IUserManagementService> users, LogRecorder? logs = null)
    {
        return new CreateSuperAdminHandler(
            users.Object,
            new FirstRunSetupToken(Configuration(ConfiguredToken), NullLogger<FirstRunSetupToken>.Instance, TextWriter.Null),
            (logs ?? new LogRecorder()).For<CreateSuperAdminHandler>());
    }

    private static CreateSuperAdminCommand Command(string? setupToken)
    {
        return new CreateSuperAdminCommand(new CreateSuperAdminRequest
        {
            UserName = "admin",
            Password = StrongPassword,
            ConfirmPassword = StrongPassword,
            SetupToken = setupToken
        });
    }

    private static object SetupBody(string setupToken)
    {
        return new { userName = "admin", password = StrongPassword, confirmPassword = StrongPassword, setupToken };
    }

    private static BeaconConfiguration Configuration(string? setupToken)
    {
        var configuration = new BeaconConfiguration();
        configuration.EnableUserManagement(x => x.SetupToken = setupToken);

        return configuration;
    }

    private static ServiceProvider BuildBeaconServices(string setupToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
                ["Beacon:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
                ["Beacon:UserManagement:SetupToken"] = setupToken
            })
            .Build();

        services.AddBeaconServices(configuration, x =>
        {
            x.AddBeaconScheduler<NoOpScheduler>();
            x.EnableUserManagement();
        });

        return services.BuildServiceProvider();
    }

    private static string TokenFrom(SignalingWriter console)
    {
        var line = console.ToString().Trim();

        return line[(line.LastIndexOf(' ') + 1)..];
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Hosts the setup endpoints. Without <paramref name="mediator"/> the real <see cref="CreateSuperAdminHandler"/>
    /// serves the POST, with the host's <see cref="FirstRunSetupToken"/>. With <paramref name="mapLoginProbe"/> a probe
    /// endpoint behind the login throttle shares the same limiter.
    /// </summary>
    private static async Task<WebApplication> StartSetupHostAsync(
        IUserManagementService? users,
        LogRecorder? logs = null,
        string? setupToken = ConfiguredToken,
        TextWriter? console = null,
        IMediator? mediator = null,
        bool mapLoginProbe = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (logs != null)
        {
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
        }

        // A limiter per host: the process-wide fallback would share its window with every other test.
        builder.Services.AddSingleton(new LoginRateLimiter());
        if (users != null)
        {
            builder.Services.AddSingleton(users);
        }

        builder.Services.AddSingleton(Mock.Of<IRoleService>());
        builder.Services.AddSingleton(sp => new FirstRunSetupToken(
            Configuration(setupToken),
            sp.GetRequiredService<ILogger<FirstRunSetupToken>>(),
            console ?? TextWriter.Null));
        builder.Services.AddSingleton(sp => mediator ?? ForwardToHandler(sp));

        var app = builder.Build();
        app.MapSetupEndpoints("/beacon");
        if (mapLoginProbe)
        {
            app.MapPost("/login-probe", () => Results.Ok()).AddEndpointFilter<LoginRateLimitFilter>();
        }

        await app.StartAsync();

        return app;
    }

    private static IMediator ForwardToHandler(IServiceProvider services)
    {
        var handler = new CreateSuperAdminHandler(
            services.GetRequiredService<IUserManagementService>(),
            services.GetRequiredService<FirstRunSetupToken>(),
            services.GetRequiredService<ILogger<CreateSuperAdminHandler>>());
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<CreateSuperAdminCommand>(), It.IsAny<CancellationToken>()))
            .Returns((IRequest<CreateSuperAdminResult> request, CancellationToken ct) =>
                handler.Handle((CreateSuperAdminCommand)request, ct));

        return mediator.Object;
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }

    /// <summary>A console stand-in that completes <see cref="FirstLine"/> with the first line written to it.</summary>
    private sealed class SignalingWriter : StringWriter
    {
        private readonly TaskCompletionSource<string> _firstLine = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> FirstLine => _firstLine.Task;

        public override void WriteLine(string? value)
        {
            lock (this)
            {
                base.WriteLine(value);
            }

            _firstLine.TrySetResult(value ?? string.Empty);
        }

        public override string ToString()
        {
            lock (this)
            {
                return base.ToString();
            }
        }
    }
}
