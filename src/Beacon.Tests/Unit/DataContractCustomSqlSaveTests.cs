using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataQuality.CreateDataContract;
using Beacon.Core.Handlers.DataQuality.DeleteDataContract;
using Beacon.Core.Handlers.DataQuality.EvaluateDataContract;
using Beacon.Core.Handlers.DataQuality.SetDataContractOwner;
using Beacon.Core.Handlers.DataQuality.UpdateDataContract;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Saving a data contract: its owner is the caller that created it, and only the owner or an Admin changes, disables,
/// retargets or deletes it. A contract that carries a CustomSql rule (§1.5) is an Admin's to create, change or delete;
/// its SQL must be a single read-only SELECT that Beacon can cap at one row; it never targets a missing, non-database
/// or host-managed data source. A rejected save writes nothing, purges no rule history and leaves the schedule alone.
/// </summary>
[TestFixture]
public class DataContractCustomSqlSaveTests
{
    private const int ContractId = 42;
    private const int DataSourceId = 7;
    private const string WriteBatch = "UPDATE accounts SET balance = 0; SELECT 'IBAN123' AS actual_value, 1 AS passed";
    private const string ReadOnlySql = "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END AS passed FROM accounts";

    private Store _store = null!;
    private Mock<IBeaconScheduler> _scheduler = null!;
    private Mock<IDataQualityEvaluationService> _evaluation = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new Store();
        _scheduler = new Mock<IBeaconScheduler>();
        _evaluation = new Mock<IDataQualityEvaluationService>();
        _evaluation
            .Setup(x => x.EvaluateContractAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DataQualityEvaluationData { DataContractId = ContractId });
    }

    // --- create -------------------------------------------------------------------------------------------------

    [Test]
    public async Task Create_CustomSqlByANonAdmin_IsForbiddenBeforeAnyLookup()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict);
        var editor = Interactive(RoleService.RoleNames.Editor);
        var handler = new CreateDataContractHandler(factory.Object, _scheduler.Object, UserContext(editor), TestSqlGate.Create(), new BeaconActorAccessor(Accessor(editor), factory.Object));

        var act = () => handler.Handle(CreateCommand(CustomSqlRule(ReadOnlySql)), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        factory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
        _scheduler.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Create_CustomSqlByAnExecuteScopedApiKey_IsForbidden()
    {
        var handler = CreateHandler(OrdinarySource(), ApiKey("Execute"));

        var act = () => handler.Handle(CreateCommand(CustomSqlRule(ReadOnlySql)), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>("an API key carries no role, so it is never an Admin");
        AssertNothingWritten();
    }

    [Test]
    public async Task Create_DisabledCustomSqlByANonAdmin_IsForbidden()
    {
        var handler = CreateHandler(OrdinarySource(), Interactive(RoleService.RoleNames.Editor));

        var act = () => handler.Handle(CreateCommand(CustomSqlRule(ReadOnlySql, isEnabled: false)), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>("a disabled rule can be switched on later");
        AssertNothingWritten();
    }

    [TestCase("UPDATE accounts SET balance = 0", "Write operations are not allowed*")]
    [TestCase("DROP TABLE accounts", "Write operations are not allowed*")]
    [TestCase(WriteBatch, "Write operations are not allowed*")]
    [TestCase("SELECT 1 AS passed; SELECT 2 AS passed", "Multiple SQL statements are not allowed*")]
    [TestCase("SELECT * INTO accounts_copy FROM accounts", "SELECT ... INTO is not allowed*")]
    public async Task Create_NonReadOnlyCustomSql_IsRejected(string sql, string reason)
    {
        var handler = CreateHandler(OrdinarySource(), Admin());

        var act = () => handler.Handle(CreateCommand(CustomSqlRule(sql)), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Custom SQL rule 'custom': Rule SQL was rejected: {reason}");
        AssertNothingWritten();
    }

    [Test]
    public async Task Create_DisabledNonReadOnlyCustomSql_IsRejected()
    {
        var handler = CreateHandler(OrdinarySource(), Admin());

        var act = () => handler.Handle(CreateCommand(CustomSqlRule(WriteBatch, isEnabled: false)), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Rule SQL was rejected: Write operations are not allowed*");
        AssertNothingWritten();
    }

    [TestCase(DatabaseEngineType.PostgreSQL, "SELECT 1 AS passed FROM accounts LIMIT 5")]
    [TestCase(DatabaseEngineType.MySQL, "SELECT 1 AS passed FROM accounts LIMIT 1")]
    [TestCase(DatabaseEngineType.MSSQL, "SELECT TOP 5 1 AS passed FROM accounts")]
    [TestCase(DatabaseEngineType.MSSQL, "SELECT 1 AS passed FROM accounts ORDER BY id OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY")]
    public async Task Create_CustomSqlWithItsOwnRowLimit_IsRejected(DatabaseEngineType engine, string sql)
    {
        var handler = CreateHandler(OrdinarySource(engine), Admin());

        var act = () => handler.Handle(CreateCommand(CustomSqlRule(sql)), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Custom SQL rule 'custom': {DataQualityRuleGuard.OwnRowLimitMessage}");
        AssertNothingWritten();
    }

    [Test]
    public async Task Create_CustomSqlOnAHostSource_IsRejected()
    {
        var handler = CreateHandler(HostSource(), Admin());

        var act = () => handler.Handle(CreateCommand(CustomSqlRule("SELECT 1 AS passed FROM \"Customer\"")), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(DataQualityRuleGuard.HostManagedCustomSqlMessage);
        AssertNothingWritten();
    }

    [Test]
    public async Task Create_CustomSqlOnAMissingDataSource_IsRejected()
    {
        var handler = CreateHandler(dataSource: null, Admin());

        var act = () => handler.Handle(CreateCommand(CustomSqlRule(ReadOnlySql)), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("The contract's data source was not found.");
        AssertNothingWritten();
    }

    [Test]
    public async Task Create_CustomSqlOnANonDatabaseSource_IsRejected()
    {
        var handler = CreateHandler(OrdinarySource(type: DataSourceType.Api), Admin());

        var act = () => handler.Handle(CreateCommand(CustomSqlRule(ReadOnlySql)), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Custom SQL rules need a database data source.");
        AssertNothingWritten();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("{}")]
    [TestCase("not json")]
    [TestCase("[\"SELECT 1 AS passed\"]")]
    [TestCase("{\"sql\":123}")]
    [TestCase("{\"sql\":null}")]
    [TestCase("{\"sql\":\"   \"}")]
    public async Task Create_CustomSqlConfigurationWithoutSql_IsRejected(string? configuration)
    {
        var handler = CreateHandler(OrdinarySource(), Admin());
        var rule = new DataContractRuleData
        {
            Name = "custom",
            RuleType = DataContractRuleType.CustomSql,
            Configuration = configuration!,
            IsEnabled = true
        };

        var act = () => handler.Handle(CreateCommand(rule), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Custom SQL rule 'custom' needs a \"sql\" string in its configuration.");
        AssertNothingWritten();
    }

    [Test]
    public async Task Create_ReadOnlyCustomSqlByAnAdmin_IsSaved()
    {
        var handler = CreateHandler(OrdinarySource(), Admin());

        await handler.Handle(CreateCommand(CustomSqlRule(ReadOnlySql)), CancellationToken.None);

        _store.Added.OfType<DataContract>().Should().ContainSingle()
            .Which.Rules.Should().ContainSingle(x => x.RuleType == DataContractRuleType.CustomSql);
        _store.Saves.Should().Be(1);
    }

    [Test]
    public async Task Create_GeneratedRulesByANonAdmin_AreSaved()
    {
        var handler = CreateHandler(OrdinarySource(), Interactive(RoleService.RoleNames.Editor));

        await handler.Handle(CreateCommand(VolumeRule()), CancellationToken.None);

        _store.Added.OfType<DataContract>().Should().ContainSingle();
        _store.Saves.Should().Be(1);
    }

    // --- update -------------------------------------------------------------------------------------------------

    [Test]
    public async Task Update_ANonAdminChangingAContractThatCarriesCustomSql_IsForbidden()
    {
        var handler = UpdateHandler(Interactive(RoleService.RoleNames.Editor), OrdinarySource(), ExistingCustomSqlRule());

        var act = () => handler.Handle(UpdateCommand(VolumeRule()), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>("removing or rewriting an Admin's custom SQL is an Admin's change too");
        AssertNothingWritten();
    }

    [Test]
    public async Task Update_ANonAdminAddingCustomSql_IsForbidden()
    {
        var handler = UpdateHandler(Interactive(RoleService.RoleNames.Editor), OrdinarySource());

        var act = () => handler.Handle(UpdateCommand(CustomSqlRule(ReadOnlySql)), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        AssertNothingWritten();
    }

    [Test]
    public async Task Update_ACustomSqlRuleFoundOnlyInsideTheTransaction_IsStillAnAdminsChange()
    {
        // The pre-transaction check sees no CustomSql rule, the contract loaded inside the transaction does.
        var contract = Contract(ExistingCustomSqlRule());
        var handler = new UpdateDataContractHandler(
            ContextFactory(OrdinarySource(), contract, rules: []),
            _scheduler.Object,
            UserContext(Interactive(RoleService.RoleNames.Editor)),
            TestSqlGate.Create(),
            Actor(Interactive(RoleService.RoleNames.Editor)),
            NullLogger<UpdateDataContractHandler>.Instance);

        var act = () => handler.Handle(UpdateCommand(VolumeRule()), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _store.Saves.Should().Be(0);
        _store.Commits.Should().Be(0);
        _store.PurgedResults.Should().Be(0);
        _scheduler.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Update_NonReadOnlyCustomSqlByAnAdmin_IsRejected()
    {
        var handler = UpdateHandler(Admin(), OrdinarySource());

        var act = () => handler.Handle(UpdateCommand(CustomSqlRule(WriteBatch)), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Rule SQL was rejected: Write operations are not allowed*");
        AssertNothingWritten();
    }

    [Test]
    public async Task Update_CustomSqlOntoAHostSource_IsRejected()
    {
        var handler = UpdateHandler(Admin(), HostSource());

        var act = () => handler.Handle(UpdateCommand(CustomSqlRule(ReadOnlySql)), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(DataQualityRuleGuard.HostManagedCustomSqlMessage);
        AssertNothingWritten();
    }

    [Test]
    public async Task Update_CustomSqlOntoAMissingDataSource_IsRejected()
    {
        var handler = UpdateHandler(Admin(), dataSource: null);

        var act = () => handler.Handle(UpdateCommand(CustomSqlRule(ReadOnlySql)), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("The contract's data source was not found.");
        AssertNothingWritten();
    }

    [Test]
    public async Task Update_ReadOnlyCustomSqlByAnAdmin_RewritesTheRules()
    {
        var oldRule = ExistingCustomSqlRule();
        var contract = Contract(oldRule);
        var handler = new UpdateDataContractHandler(
            ContextFactory(OrdinarySource(), contract, rules: [oldRule], results: [new DataQualityRuleResult { DataContractRuleId = oldRule.Id }]),
            _scheduler.Object,
            UserContext(Admin()),
            TestSqlGate.Create(),
            Actor(Admin()),
            NullLogger<UpdateDataContractHandler>.Instance);

        await handler.Handle(UpdateCommand(CustomSqlRule("SELECT 1 AS passed")), CancellationToken.None);

        contract.Rules.Should().ContainSingle()
            .Which.Configuration.Should().Be(JsonSerializer.Serialize(new { sql = "SELECT 1 AS passed" }));
        _store.PurgedResults.Should().Be(1, "the old rule's results go with it");
        _store.Saves.Should().Be(1);
        _store.Commits.Should().Be(1);
        _scheduler.Verify(x => x.RemoveDataQualityJob(ContractId, "contract"), Times.Once);
    }

    [Test]
    public async Task Update_AnAdminReplacingCustomSqlWithGeneratedRules_IsSaved()
    {
        var oldRule = ExistingCustomSqlRule();
        var contract = Contract(oldRule);
        var handler = new UpdateDataContractHandler(
            ContextFactory(OrdinarySource(), contract, rules: [oldRule]),
            _scheduler.Object,
            UserContext(Admin()),
            TestSqlGate.Create(),
            Actor(Admin()),
            NullLogger<UpdateDataContractHandler>.Instance);

        await handler.Handle(UpdateCommand(VolumeRule()), CancellationToken.None);

        contract.Rules.Should().ContainSingle().Which.RuleType.Should().Be(DataContractRuleType.Volume);
        _store.Saves.Should().Be(1);
        _store.Commits.Should().Be(1);
    }

    // --- delete -------------------------------------------------------------------------------------------------

    [Test]
    public async Task Delete_ANonAdminDeletingAContractThatCarriesCustomSql_IsForbidden()
    {
        var contract = Contract(ExistingCustomSqlRule());
        var handler = DeleteHandler(Interactive(RoleService.RoleNames.Editor), contract);

        var act = () => handler.Handle(new DeleteDataContractCommand(ContractId), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        contract.ArchivedTime.Should().BeNull();
        _store.Saves.Should().Be(0);
        _scheduler.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Delete_AnAdminDeletingAContractThatCarriesCustomSql_ArchivesIt()
    {
        var contract = Contract(ExistingCustomSqlRule());
        var handler = DeleteHandler(Admin(), contract);

        await handler.Handle(new DeleteDataContractCommand(ContractId), CancellationToken.None);

        contract.ArchivedTime.Should().NotBeNull();
        _store.Saves.Should().Be(1);
        _scheduler.Verify(x => x.RemoveDataQualityJob(ContractId, "contract"), Times.Once);
    }

    [Test]
    public async Task Delete_ANonAdminDeletingAContractWithGeneratedRules_ArchivesIt()
    {
        var contract = Contract(ExistingRule(DataContractRuleType.Volume, "{\"minRows\":1}"));
        var handler = DeleteHandler(Interactive(RoleService.RoleNames.Editor), contract);

        await handler.Handle(new DeleteDataContractCommand(ContractId), CancellationToken.None);

        contract.ArchivedTime.Should().NotBeNull();
    }

    // --- ownership ----------------------------------------------------------------------------------------------

    [Test]
    public async Task Create_TheOwnerIsTheSignedInCaller_NotAValueFromTheBody()
    {
        const string body = """
            {"dataSourceId":7,"schemaName":"sales","tableName":"orders","name":"contract","cronExpression":"0 0 * * *",
             "isEnabled":false,"ownerUserId":"someone-else","alertOnFailure":false,"failureThresholdScore":80,"rules":[]}
            """;
        var command = JsonSerializer.Deserialize<CreateDataContractCommand>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var handler = CreateHandler(OrdinarySource(), Interactive(RoleService.RoleNames.Editor));

        await handler.Handle(command with { Rules = [VolumeRule()] }, CancellationToken.None);

        _store.Added.OfType<DataContract>().Should().ContainSingle().Which.OwnerUserId.Should().Be("1");
    }

    [TestCase("someone-else")]
    [TestCase(null)]
    public async Task Update_BySomeoneOtherThanTheOwner_IsForbiddenBeforeAnyChange(string? owner)
    {
        var contract = Contract(ExistingRule(DataContractRuleType.Volume, "{\"minRows\":1}"));
        contract.OwnerUserId = owner;
        contract.IsEnabled = true;
        var editor = Interactive(RoleService.RoleNames.Editor);
        var handler = new UpdateDataContractHandler(
            ContextFactory(OrdinarySource(), contract, contract.Rules),
            _scheduler.Object,
            UserContext(editor),
            TestSqlGate.Create(),
            Actor(editor),
            NullLogger<UpdateDataContractHandler>.Instance);

        // Disabling it and pointing it at another data source.
        var act = () => handler.Handle(UpdateCommand(VolumeRule()) with { DataSourceId = 99, IsEnabled = false }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*owner or an Admin*");
        contract.IsEnabled.Should().BeTrue();
        contract.DataSourceId.Should().Be(DataSourceId);
        AssertNothingWritten();
    }

    [Test]
    public async Task Update_ByAnAdminWhoIsNotTheOwner_IsSaved()
    {
        var contract = Contract(ExistingRule(DataContractRuleType.Volume, "{\"minRows\":1}"));
        contract.OwnerUserId = "someone-else";
        var handler = new UpdateDataContractHandler(
            ContextFactory(OrdinarySource(), contract, contract.Rules),
            _scheduler.Object,
            UserContext(Admin()),
            TestSqlGate.Create(),
            Actor(Admin()),
            NullLogger<UpdateDataContractHandler>.Instance);

        await handler.Handle(UpdateCommand(VolumeRule()) with { Name = "renamed" }, CancellationToken.None);

        contract.Name.Should().Be("renamed");
        contract.OwnerUserId.Should().Be("someone-else", "an update never changes the owner");
        _store.Commits.Should().Be(1);
    }

    [TestCase("someone-else")]
    [TestCase(null)]
    public async Task Delete_BySomeoneOtherThanTheOwner_IsForbidden(string? owner)
    {
        var contract = Contract(ExistingRule(DataContractRuleType.Volume, "{\"minRows\":1}"));
        contract.OwnerUserId = owner;
        var handler = DeleteHandler(Interactive(RoleService.RoleNames.Editor), contract);

        var act = () => handler.Handle(new DeleteDataContractCommand(ContractId), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*owner or an Admin*");
        contract.ArchivedTime.Should().BeNull();
        _scheduler.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Delete_ByAnAdminWhoIsNotTheOwner_ArchivesIt()
    {
        var contract = Contract(ExistingRule(DataContractRuleType.Volume, "{\"minRows\":1}"));
        contract.OwnerUserId = "someone-else";
        var handler = DeleteHandler(Admin(), contract);

        await handler.Handle(new DeleteDataContractCommand(ContractId), CancellationToken.None);

        contract.ArchivedTime.Should().NotBeNull();
    }

    [Test]
    public async Task Create_ByACallerWithoutAResolvableUser_IsRefused_AndNoContractIsStoredWithoutAnOwner()
    {
        var handler = CreateHandler(OrdinarySource(), WithoutId(RoleService.RoleNames.Editor));

        var act = () => handler.Handle(CreateCommand(VolumeRule()), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no resolvable user*");
        AssertNothingWritten();
    }

    [Test]
    public async Task AMissingContract_IsRefusedToAnEditorLikeAContractThatIsNotTheirs_AndReportedAsMissingToAnAdmin()
    {
        // Each handler is built just before it runs: the request's user lives in the ambient HttpContext.
        var editor = Interactive(RoleService.RoleNames.Editor);
        Func<Task> update = () => new UpdateDataContractHandler(ContextFactory(OrdinarySource()), _scheduler.Object, UserContext(editor), TestSqlGate.Create(), Actor(editor), NullLogger<UpdateDataContractHandler>.Instance)
            .Handle(UpdateCommand(VolumeRule()), CancellationToken.None);
        Func<Task> delete = () => new DeleteDataContractHandler(ContextFactory(OrdinarySource()), _scheduler.Object, UserContext(editor), Actor(editor), NullLogger<DeleteDataContractHandler>.Instance)
            .Handle(new DeleteDataContractCommand(ContractId), CancellationToken.None);
        Func<Task> evaluate = () => EvaluateHandler(editor, contract: null).Handle(new EvaluateDataContractCommand(ContractId), CancellationToken.None);
        Func<Task> adminDelete = () => new DeleteDataContractHandler(ContextFactory(OrdinarySource()), _scheduler.Object, UserContext(Admin()), Actor(Admin()), NullLogger<DeleteDataContractHandler>.Instance)
            .Handle(new DeleteDataContractCommand(ContractId), CancellationToken.None);

        await update.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*owner or an Admin*");
        await delete.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*owner or an Admin*");
        await evaluate.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*owner or an Admin*");
        await adminDelete.Should().ThrowAsync<BeaconException>().WithMessage($"Data contract {ContractId} not found");
        _evaluation.VerifyNoOtherCalls();
        AssertNothingWritten();
    }

    [Test]
    public async Task AnOwnerlessContract_IsNotTheContractOfACallerWithoutAUser()
    {
        var contract = Contract(ExistingRule(DataContractRuleType.Volume, "{\"minRows\":1}"));
        contract.OwnerUserId = null;
        var caller = WithoutId(RoleService.RoleNames.Editor);
        Func<Task> update = () => new UpdateDataContractHandler(ContextFactory(OrdinarySource(), contract, contract.Rules), _scheduler.Object, UserContext(caller), TestSqlGate.Create(), Actor(caller), NullLogger<UpdateDataContractHandler>.Instance)
            .Handle(UpdateCommand(VolumeRule()), CancellationToken.None);
        Func<Task> delete = () => DeleteHandler(caller, contract).Handle(new DeleteDataContractCommand(ContractId), CancellationToken.None);
        Func<Task> evaluate = () => EvaluateHandler(caller, contract).Handle(new EvaluateDataContractCommand(ContractId), CancellationToken.None);

        await update.Should().ThrowAsync<UnauthorizedAccessException>();
        await delete.Should().ThrowAsync<UnauthorizedAccessException>();
        await evaluate.Should().ThrowAsync<UnauthorizedAccessException>();
        contract.ArchivedTime.Should().BeNull();
        _evaluation.VerifyNoOtherCalls();
        AssertNothingWritten();
    }

    [TestCase("1", RoleService.RoleNames.Editor, true)]
    [TestCase("someone-else", RoleService.RoleNames.Admin, true)]
    [TestCase("someone-else", RoleService.RoleNames.Editor, false)]
    [TestCase(null, RoleService.RoleNames.Editor, false)]
    public async Task Evaluate_IsTheOwnersOrAnAdmins(string? owner, string role, bool allowed)
    {
        var contract = Contract(ExistingRule(DataContractRuleType.Volume, "{\"minRows\":1}"));
        contract.OwnerUserId = owner;
        var handler = EvaluateHandler(Interactive(role), contract);

        var act = () => handler.Handle(new EvaluateDataContractCommand(ContractId), CancellationToken.None);

        if (allowed)
        {
            await act.Should().NotThrowAsync();
            _evaluation.Verify(x => x.EvaluateContractAsync(ContractId, It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*owner or an Admin*");
            _evaluation.VerifyNoOtherCalls();
        }
    }

    [Test]
    public async Task SetOwner_ByAnAdmin_MakesAnActiveUserTheOwner_OfAContractWithoutOne()
    {
        var contract = Contract();
        contract.OwnerUserId = null;

        await SetOwnerHandler(Admin(), ContextFactory(OrdinarySource(), contract, users: Users())).Handle(new SetDataContractOwnerCommand(ContractId, 3), CancellationToken.None);

        contract.OwnerUserId.Should().Be("ext-maria");
        _store.Saves.Should().Be(0, "the owner is written by one conditional update, not by tracked changes");
    }

    [Test]
    public async Task SetOwner_ByTheOwner_IsForbidden()
    {
        var contract = Contract();

        var act = () => SetOwnerHandler(Interactive(RoleService.RoleNames.Editor), ContextFactory(OrdinarySource(), contract, users: Users()))
            .Handle(new SetDataContractOwnerCommand(ContractId, 3), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        contract.OwnerUserId.Should().Be("1");
    }

    [TestCase(4)]
    [TestCase(99)]
    public async Task SetOwner_ToSomeoneWhoIsNotAnActiveUser_IsRejected(int userId)
    {
        var contract = Contract();

        var act = () => SetOwnerHandler(Admin(), ContextFactory(OrdinarySource(), contract, users: Users()))
            .Handle(new SetDataContractOwnerCommand(ContractId, userId), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*existing, enabled user*");
        contract.OwnerUserId.Should().Be("1");
    }

    [Test]
    public async Task SetOwner_OfAMissingContract_IsReportedAsMissing()
    {
        var act = () => SetOwnerHandler(Admin(), ContextFactory(OrdinarySource(), users: Users()))
            .Handle(new SetDataContractOwnerCommand(ContractId, 3), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconException>().WithMessage($"Data contract {ContractId} not found");
    }

    // --- helpers ------------------------------------------------------------------------------------------------

    private EvaluateDataContractHandler EvaluateHandler(ClaimsPrincipal user, DataContract? contract)
    {
        return new EvaluateDataContractHandler(_evaluation.Object, ContextFactory(OrdinarySource(), contract), Actor(user), NullLogger<EvaluateDataContractHandler>.Instance);
    }

    private SetDataContractOwnerHandler SetOwnerHandler(ClaimsPrincipal user, IDbContextFactory<BeaconContext> factory)
    {
        return new SetDataContractOwnerHandler(factory, Actor(user), NullLogger<SetDataContractOwnerHandler>.Instance);
    }

    private static List<BeaconUser> Users()
    {
        return
        [
            new BeaconUser { Id = 3, ExternalId = "ext-maria", UserName = "maria", IsEnabled = true },
            new BeaconUser { Id = 4, ExternalId = "ext-noor", UserName = "noor", IsEnabled = false }
        ];
    }

    private void AssertNothingWritten()
    {
        _store.Added.Should().BeEmpty();
        _store.Saves.Should().Be(0);
        _store.TransactionsBegun.Should().Be(0);
        _store.PurgedResults.Should().Be(0);
        _scheduler.VerifyNoOtherCalls();
    }

    private CreateDataContractHandler CreateHandler(DataSource? dataSource, ClaimsPrincipal user)
    {
        return new CreateDataContractHandler(ContextFactory(dataSource), _scheduler.Object, UserContext(user), TestSqlGate.Create(), Actor(user));
    }

    private UpdateDataContractHandler UpdateHandler(ClaimsPrincipal user, DataSource? dataSource, params DataContractRule[] existingRules)
    {
        return new UpdateDataContractHandler(
            ContextFactory(dataSource, Contract(existingRules), [.. existingRules]),
            _scheduler.Object,
            UserContext(user),
            TestSqlGate.Create(),
            Actor(user),
            NullLogger<UpdateDataContractHandler>.Instance);
    }

    private DeleteDataContractHandler DeleteHandler(ClaimsPrincipal user, DataContract contract)
    {
        return new DeleteDataContractHandler(ContextFactory(OrdinarySource(), contract, contract.Rules), _scheduler.Object, UserContext(user), Actor(user), NullLogger<DeleteDataContractHandler>.Instance);
    }

    private IDbContextFactory<BeaconContext> ContextFactory(
        DataSource? dataSource,
        DataContract? contract = null,
        List<DataContractRule>? rules = null,
        List<DataQualityRuleResult>? results = null,
        List<BeaconUser>? users = null)
    {
        var resultRows = results ?? [];
        var sets = new Dictionary<Type, object>
        {
            [typeof(DataSource)] = MemorySet<DataSource>(dataSource == null ? [] : [dataSource]),
            [typeof(DataContract)] = MemorySet<DataContract>(contract == null ? [] : [contract]),
            [typeof(DataContractRule)] = MemorySet(rules ?? []),
            [typeof(BeaconUser)] = MemorySet(users ?? []),
            [typeof(DataQualityRuleResult)] = MemorySet(resultRows, deleted =>
            {
                _store.PurgedResults += deleted.Count;
                return deleted.Count;
            })
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DataQualityTestContext(sets, _store));

        return factory.Object;
    }

    private DbSet<T> MemorySet<T>(List<T> rows, Func<IReadOnlyList<T>, int>? onExecuteDelete = null) where T : class
    {
        var data = rows.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
        set.As<IQueryable<T>>()
            .Setup(x => x.Provider)
            .Returns(new TestAsyncQueryProvider<T>(data.Provider, onExecuteDelete));
        set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(data.Expression);
        set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(data.ElementType);
        set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
        set.Setup(x => x.Add(It.IsAny<T>())).Callback<T>(_store.Added.Add);
        return set.Object;
    }

    private static DataSource OrdinarySource(DatabaseEngineType engine = DatabaseEngineType.PostgreSQL, DataSourceType type = DataSourceType.Database)
    {
        return new DataSource
        {
            Id = DataSourceId,
            Name = "warehouse",
            DataSourceType = type,
            EncryptedConnectionData = "unused",
            DatabaseEngineType = engine
        };
    }

    private static DataSource HostSource()
    {
        return new DataSource
        {
            Id = DataSourceId,
            Name = "Netgiro",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "reference",
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            HostManagedKey = "efcore:Netgiro"
        };
    }

    private static DataContract Contract(params DataContractRule[] rules)
    {
        return new DataContract
        {
            Id = ContractId,
            DataSourceId = DataSourceId,
            SchemaName = "sales",
            TableName = "orders",
            Name = "contract",
            CronExpression = "0 0 * * *",
            IsEnabled = false,
            OwnerUserId = "1",
            Rules = [.. rules]
        };
    }

    private static DataContractRule ExistingCustomSqlRule()
    {
        return ExistingRule(DataContractRuleType.CustomSql, JsonSerializer.Serialize(new { sql = ReadOnlySql }));
    }

    private static DataContractRule ExistingRule(DataContractRuleType ruleType, string configuration)
    {
        return new DataContractRule
        {
            Id = 11,
            DataContractId = ContractId,
            Name = "existing",
            RuleType = ruleType,
            Configuration = configuration,
            IsEnabled = true
        };
    }

    private static DataContractRuleData CustomSqlRule(string sql, bool isEnabled = true)
    {
        return new DataContractRuleData
        {
            Name = "custom",
            RuleType = DataContractRuleType.CustomSql,
            Configuration = JsonSerializer.Serialize(new { sql }),
            IsEnabled = isEnabled
        };
    }

    private static DataContractRuleData VolumeRule()
    {
        return new DataContractRuleData
        {
            Name = "volume",
            RuleType = DataContractRuleType.Volume,
            Configuration = "{\"minRows\":1}",
            IsEnabled = true
        };
    }

    private static CreateDataContractCommand CreateCommand(DataContractRuleData rule)
    {
        return new CreateDataContractCommand(DataSourceId, "sales", "orders", "contract", null, "0 0 * * *", false, false, 80, [rule]);
    }

    private static UpdateDataContractCommand UpdateCommand(DataContractRuleData rule)
    {
        return new UpdateDataContractCommand(ContractId, DataSourceId, "sales", "orders", "contract", null, "0 0 * * *", false, false, 80, [rule]);
    }

    private BeaconActorAccessor Actor(ClaimsPrincipal user)
    {
        return new BeaconActorAccessor(Accessor(user), ContextFactory(dataSource: null));
    }

    private static IBeaconUserContext UserContext(ClaimsPrincipal user)
    {
        return new HttpContextUserContext(Accessor(user));
    }

    private static HttpContextAccessor Accessor(ClaimsPrincipal user)
    {
        return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } };
    }

    private static ClaimsPrincipal Admin()
    {
        return Interactive(RoleService.RoleNames.Admin);
    }

    private static ClaimsPrincipal WithoutId(string role)
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Cookies"));
    }

    private static ClaimsPrincipal Interactive(string role)
    {
        Claim[] claims = [new(ClaimTypes.NameIdentifier, "1"), new(ClaimTypes.Role, role)];

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }

    private static ClaimsPrincipal ApiKey(string scope)
    {
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, "1"),
            new(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod),
            new(McpCallerClaimTypes.Scope, scope)
        ];

        return new ClaimsPrincipal(new ClaimsIdentity(claims, McpCallerClaimTypes.ApiKeyAuthenticationType));
    }

    /// <summary>What the handlers wrote: added entities, saves, transactions and purged rule results.</summary>
    private sealed class Store
    {
        public List<object> Added { get; } = [];
        public int Saves { get; set; }
        public int TransactionsBegun { get; set; }
        public int Commits { get; set; }
        public int PurgedResults { get; set; }
    }

    /// <summary>
    /// A <see cref="BeaconContext"/> over in-memory sets (§4.7: no in-memory provider) whose transactions and saves are
    /// recorded in <see cref="Store"/> instead of reaching a database.
    /// </summary>
    private sealed class DataQualityTestContext : BeaconContext
    {
        private static readonly DbContextOptions<DataQualityTestContext> Options =
            new DbContextOptionsBuilder<DataQualityTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly IReadOnlyDictionary<Type, object> _sets;
        private readonly Store _store;
        private readonly Mock<DatabaseFacade> _database;

        public DataQualityTestContext(IReadOnlyDictionary<Type, object> sets, Store store)
            : base(Options, "beacon")
        {
            _sets = sets;
            _store = store;
            var transaction = new Mock<IDbContextTransaction>();
            transaction
                .Setup(x => x.CommitAsync(It.IsAny<CancellationToken>()))
                .Callback(() => _store.Commits++)
                .Returns(Task.CompletedTask);
            _database = new Mock<DatabaseFacade>(this);
            _database
                .Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
                .Callback(() => _store.TransactionsBegun++)
                .ReturnsAsync(transaction.Object);
        }

        public override DatabaseFacade Database => _database.Object;

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class =>
            _sets.TryGetValue(typeof(TEntity), out var set) ? (DbSet<TEntity>)set : base.Set<TEntity>();

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            _store.Saves++;
            return Task.FromResult(0);
        }
    }
}
