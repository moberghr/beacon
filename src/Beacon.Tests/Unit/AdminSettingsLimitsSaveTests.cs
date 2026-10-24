using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.AdminSettings;
using Beacon.Core.Models;
using Beacon.Core.Models.Settings;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The LLM concurrency limit, the requests and tokens per minute and whether those are enforced are stored and applied
/// only by an Admin's save. Anyone else's save neither writes nor applies them, whatever it carries, so the host's limits
/// and an Admin's latest choice stay in force; a save that changes them is still refused.
/// </summary>
[TestFixture]
public class AdminSettingsLimitsSaveTests
{
    private static readonly string[] LimitKeys =
    [
        "LLM.MaxConcurrentRequests",
        "LLM.RequestsPerMinute",
        "LLM.TokensPerMinute",
        "LLM.RateLimitsEnabled"
    ];

    private static readonly ProviderLimits HostLimits = new()
    {
        MaxConcurrentRequests = 1,
        RequestsPerMinute = 10,
        TokensPerMinute = 2000,
        RateLimitsEnabled = true
    };

    [Test]
    public async Task ASecurityAdminSave_EchoingTheDisplayedLimits_WritesNoLimits_AndKeepsTheHostLimits()
    {
        var store = new SettingsRows();
        var (manager, queue) = Llm();
        var service = Service(store, manager);
        var displayed = await service.GetSettingsAsync(CancellationToken.None);

        await Update(service, SecurityAdmin()).Handle(Echo(displayed, model: "gpt-next"), CancellationToken.None);

        store.Rows.Select(x => x.Key).Should().Contain("LLM.Model").And.NotContain(LimitKeys);
        queue.AppliedLimits.Should().BeEquivalentTo(HostLimits, x => x.ExcludingMissingMembers());
        var stored = await Service(store).GetSettingsAsync(CancellationToken.None);
        stored.LlmLimitsStored.Should().BeFalse();

        var (restarted, restartedQueue) = Llm();
        await new StoredSettingsApplier(Service(store), new BeaconConfiguration(), NullLogger<StoredSettingsApplier>.Instance, restarted)
            .ApplyAsync(CancellationToken.None);
        restartedQueue.AppliedLimits.Should().BeEquivalentTo(HostLimits, x => x.ExcludingMissingMembers());
        restarted.CurrentConfiguration.Limits.Should().Be(HostLimits);
    }

    [Test]
    public async Task ASecurityAdminSave_ChangingADisplayedLimit_IsForbidden_AndWritesNothing()
    {
        var store = new SettingsRows();
        var (manager, queue) = Llm();
        var service = Service(store, manager);
        var displayed = await service.GetSettingsAsync(CancellationToken.None);

        var act = () => Update(service, SecurityAdmin()).Handle(
            Echo(displayed, model: "gpt-next") with { LlmMaxConcurrentRequests = 2 },
            CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UpdateAdminSettingsHandler.LimitsAdminOnlyMessage);
        store.Rows.Should().BeEmpty();
        queue.AppliedLimits.Should().BeEquivalentTo(HostLimits, x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task ASecurityAdminSave_BasedOnAnEarlierRead_KeepsTheEnforcementAnAdminTurnedOnMeanwhile()
    {
        var store = new SettingsRows();
        var (manager, queue) = Llm();
        var service = Service(store, manager);
        var earlierRead = await service.GetSettingsAsync(CancellationToken.None);

        await Update(service, TestPermissions.Admin).Handle(
            Echo(earlierRead, model: "gpt") with { LlmRateLimitsEnabled = true, LlmRequestsPerMinute = 30 },
            CancellationToken.None);
        await Update(new EarlierRead(service, earlierRead), SecurityAdmin()).Handle(
            Echo(earlierRead, model: "gpt-next") with { LlmRateLimitsEnabled = false },
            CancellationToken.None);

        var stored = await Service(store).GetSettingsAsync(CancellationToken.None);
        stored.Should().BeEquivalentTo(new { LlmModel = "gpt-next", LlmRateLimitsEnabled = true, LlmRequestsPerMinute = 30, LlmLimitsStored = true }, x => x.ExcludingMissingMembers());
        queue.AppliedLimits.Should().BeEquivalentTo(new { RateLimitsEnabled = true, RequestsPerMinute = 30 }, x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task AnAdminSave_WritesAndAppliesTheLimits()
    {
        var store = new SettingsRows();
        var (manager, queue) = Llm();
        var service = Service(store, manager);
        var displayed = await service.GetSettingsAsync(CancellationToken.None);

        await Update(service, TestPermissions.Admin).Handle(
            Echo(displayed, model: "gpt") with { LlmMaxConcurrentRequests = 3, LlmRateLimitsEnabled = false },
            CancellationToken.None);

        store.Rows.Select(x => x.Key).Should().Contain(LimitKeys);
        queue.AppliedLimits.Should().BeEquivalentTo(new { MaxConcurrentRequests = 3, RateLimitsEnabled = false }, x => x.ExcludingMissingMembers());
        (await Service(store).GetSettingsAsync(CancellationToken.None)).LlmLimitsStored.Should().BeTrue();
    }

    private static UpdateAdminSettingsCommand Echo(AppSettingsData displayed, string model)
    {
        return new UpdateAdminSettingsCommand(
            displayed.BaseUrl,
            displayed.LlmProvider,
            model,
            displayed.LlmFastModel,
            displayed.LlmRegion,
            displayed.LlmBedrockAuthMode,
            null,
            null,
            null,
            null,
            null,
            displayed.LlmMaxConcurrentRequests,
            displayed.LlmTokensPerMinute,
            displayed.LlmRequestsPerMinute,
            displayed.LlmMonthlyBudget)
        {
            LlmRateLimitsEnabled = displayed.LlmRateLimitsEnabled
        };
    }

    private static UpdateAdminSettingsHandler Update(IAppSettingsService service, IBeaconPermissionAccessor permissions)
    {
        var catalog = new Mock<ILlmProviderCatalog>();
        catalog.SetupGet(x => x.AiEnabled).Returns(true);
        catalog.SetupGet(x => x.Registered).Returns([new LlmProviderInfo(AiProvider.OpenAI, true)]);

        return new UpdateAdminSettingsHandler(
            service,
            Mock.Of<IBeaconUserContext>(),
            TestProjectAccess.Actors(),
            permissions,
            catalog.Object,
            TestChangeApprovals.Llm(new ChangeGovernance.ChangeApprovalStore().Factory(), settingsService: service),
            NullLogger<UpdateAdminSettingsHandler>.Instance);
    }

    private static IBeaconPermissionAccessor SecurityAdmin() => TestPermissions.Holding(BeaconPermissions.SecurityAdmin);

    private static AppSettingsService Service(SettingsRows store, ILlmConfigurationUpdater? updater = null)
    {
        return new AppSettingsService(
            store.Factory(),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<IEncryptionService>(),
            new BeaconConfiguration(),
            new RecordingSecurityAudit(),
            updater);
    }

    private static (LlmProviderManager Manager, LlmRequestQueue Queue) Llm()
    {
        var start = new LlmConfiguration { Provider = AiProvider.OpenAI, Model = "gpt", Limits = HostLimits };
        var queue = new LlmRequestQueue(start.Limits);
        LlmProviderManager? manager = null;
        manager = new LlmProviderManager(
            start,
            TestLlmProviderFactory.Create(() => manager?.CurrentConfiguration ?? start),
            queue,
            NullLogger<LlmProviderManager>.Instance);

        return (manager, queue);
    }

    // The settings rows behind a recording context (§4.7: no database); inserted rows join the list, as the database's would.
    private sealed class SettingsRows
    {
        public List<AppSetting> Rows { get; } = [];

        public IDbContextFactory<BeaconContext> Factory()
        {
            var saved = new List<object>();
            var sets = new Dictionary<Type, object>
            {
                [typeof(AppSetting)] = RecordingBeaconContext.MemorySet(Rows, saved),
                [typeof(AppSettingHistory)] = RecordingBeaconContext.MemorySet(new List<AppSettingHistory>(), saved)
            };
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(sets, saved, onSave: () => Rows.AddRange(saved.OfType<AppSetting>().Except(Rows).ToList())));

            return factory.Object;
        }
    }

    // A caller that read the settings before an Admin's save and saves after it.
    private sealed class EarlierRead(IAppSettingsService inner, AppSettingsData read) : IAppSettingsService
    {
        public Task<AppSettingsData> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(read);

        public Task SaveSettingsAsync(AppSettingsData settings, string? userId = null, CancellationToken ct = default) =>
            inner.SaveSettingsAsync(settings, userId, ct);

        public Task SaveSettingsAsync(AppSettingsData settings, string? userId, bool saveLimits, CancellationToken ct = default) =>
            inner.SaveSettingsAsync(settings, userId, saveLimits, ct);

        public Task<List<AppSettingHistory>> GetHistoryAsync(string? key = null, CancellationToken ct = default) =>
            inner.GetHistoryAsync(key, ct);
    }
}
