using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Models.Queries;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// The checks every by-id request declares, pinned: for each <see cref="IProjectAuthorizedRequest"/>, the resource kind,
/// the property its id comes from and the action, as <c>Kind(Property):Action</c> (<c>:linking</c> for a change to shared
/// data-source state that also needs Write in every linking project, <c>:linking(Curate)</c> when it needs Curate there —
/// grounding curation). A request whose kind or action changes (Manage
/// weakened to Read, a subscription checked as its query) fails here until the manifest says so on purpose. Requests
/// whose checks depend on more than ids are pinned per branch below.
/// </summary>
[TestFixture]
public class ProjectCheckManifestTests
{
    private const int FirstSentinel = 900_001;

    private static readonly Regex IdProperty = new("Ids?(?![a-z])", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string[]> Manifest = new Dictionary<string, string[]>
    {
        ["AddSubscriptionRecipientsCommand"] = ["Subscription(SubscriptionId):Write"],
        ["AddTaskCommentCommand"] = ["Task(TaskId):Write"],
        ["ApplyDocumentationPatchCommand"] = ["DocumentationPatch(PatchId):Write:linking(Curate)"],
        ["ApproveAiActorPlanCommand"] = ["AiActorPlan(PlanId):Write"],
        ["ArchiveAiActorCommand"] = ["AiActor(ActorId):Manage"],
        ["AssignTaskCommand"] = ["Task(TaskId):Write"],
        ["CreateAiActorCommand"] = ["DataSource(DataSourceId):Write"],
        ["CreateDataContractCommand"] = ["DataSource(DataSourceId):Write"],
        ["CreateProjectCommand"] = [],
        ["CreateSchemaRelationshipCommand"] = ["DataSource(DataSourceId):Write:linking"],
        ["CreateSubscriptionCommand"] = ["Query(QueryId):Write"],
        ["DeleteDataContractCommand"] = ["DataContract(DataContractId):Manage"],
        ["DeleteSchemaRelationshipCommand"] = ["DataSource(DataSourceId):Manage:linking"],
        ["DeleteSubscriptionCommand"] = ["Subscription(SubscriptionId):Manage"],
        ["DiffQueryVersionsQuery"] = ["QueryVersion(VersionIdA):Read", "QueryVersion(VersionIdB):Read"],
        ["EvaluateDataContractCommand"] = ["DataContract(DataContractId):Run"],
        ["ExecuteAiActorThinkCycleCommand"] = ["AiActor(ActorId):Query"],
        ["ExecuteQueryPreviewCommand"] = ["Query(QueryId):Run"],
        ["ExecuteStepPreviewCommand"] = ["Query(QueryId):Run"],
        ["ExportProjectDocumentationCommand"] = ["Documentation(DocumentationId):Read"],
        ["GenerateProjectDocumentationCommand"] = ["Project(ProjectId):Curate"],
        ["GetAiActorDetailsQuery"] = ["AiActor(ActorId):Read"],
        ["GetAiActorPlanQuery"] = ["AiActorPlan(PlanId):Read"],
        ["GetApprovalDetailQuery"] = ["ApprovalRequest(RequestId):Read"],
        ["GetControlTowerSubscriptionDetailQuery"] = ["Subscription(SubscriptionId):Read"],
        ["GetDataContractDetailQuery"] = ["DataContract(DataContractId):Read"],
        ["GetDataSourceMetadataQuery"] = ["DataSource(DataSourceId):Read"],
        ["GetDataSourceMetadataRefreshQuery"] = ["DataSource(DataSourceId):Manage:linking"],
        ["GetDataSourceQuery"] = ["DataSource(Id):Read"],
        ["GetEvaluationHistoryQuery"] = ["DataContract(DataContractId):Read"],
        ["GetImportedDocumentQuery"] = ["Project(ProjectId):Read", "ImportedDocument(DocumentId):Read"],
        ["GetImportedDocumentsQuery"] = ["Project(ProjectId):Read"],
        ["GetNotificationDetailQuery"] = ["Notification(NotificationId):Read"],
        ["GetPendingPlansQuery"] = ["AiActor(ActorId):Read"],
        ["GetProjectDetailQuery"] = ["Project(ProjectId):Read"],
        ["GetProjectDocumentationQuery"] = ["Project(ProjectId):Read"],
        ["GetProjectMcpContextQuery"] = ["Project(ProjectId):Read"],
        ["GetProjectMembersQuery"] = ["Project(ProjectId):ManageMembers"],
        ["GetQueryChangeHistoryQuery"] = ["Query(QueryId):Read"],
        ["GetQueryDetailQuery"] = ["Query(QueryId):Read"],
        ["GetQueryVersionDetailQuery"] = ["QueryVersion(VersionId):Read"],
        ["GetQueryVersionsQuery"] = ["Query(QueryId):Read"],
        ["GetRepositoryScanQuery"] = ["Project(ProjectId):Write"],
        ["GetSchemaHealthQuery"] = ["DataSource(DataSourceId):Read"],
        ["GetSchemaRelationshipsQuery"] = ["DataSource(DataSourceId):Read"],
        ["GetSubscriptionAnomalyChartQuery"] = ["Subscription(SubscriptionId):Read"],
        ["GetSubscriptionDetailQuery"] = ["Subscription(SubscriptionId):Read"],
        ["GetTaskCommentsQuery"] = ["Task(TaskId):Read"],
        ["GetTaskDetailQuery"] = ["Task(Id):Read"],
        ["GetTaskExecutionsQuery"] = ["Task(TaskId):Read"],
        ["GetTaskRelatedQuery"] = ["Task(TaskId):Read"],
        ["GetTaskResultHistoryQuery"] = ["Task(TaskId):Read"],
        ["InstructDocumentationCommand"] = ["DocumentationSection(SectionId):Query"],
        ["LinkProjectDataSourcesCommand"] = ["Project(ProjectId):ManageMembers"],
        ["MoveQueryToFolderCommand"] = ["Query(QueryId):Write"],
        ["PauseAiActorCommand"] = ["AiActor(ActorId):Write"],
        ["PreviewSchemaRelationshipDiscoveryQuery"] = ["DataSource(DataSourceId):Write:linking"],
        ["ReactivateSubscriptionCommand"] = ["Subscription(SubscriptionId):Write"],
        ["RecordQueryFeedbackCommand"] = ["QuerySignal(SignalId):Query"],
        ["RefineAiActorCommand"] = ["AiActor(ActorId):Query"],
        ["RefreshDataSourceMetadataCommand"] = ["DataSource(DataSourceId):Manage:linking"],
        ["RejectAiActorPlanCommand"] = ["AiActorPlan(PlanId):Write"],
        ["RejectDocumentationPatchCommand"] = ["DocumentationPatch(PatchId):Write:linking(Curate)"],
        ["RemoveProjectMemberCommand"] = ["Project(ProjectId):ManageMembers"],
        ["RemoveSubscriptionRecipientCommand"] = ["Subscription(SubscriptionId):Write"],
        ["RequestPlanRevisionCommand"] = ["AiActorPlan(PlanId):Write"],
        ["ResolveTaskCommand"] = ["Task(Id):Write"],
        ["RestoreQueryVersionCommand"] = ["QueryVersion(VersionId):Write"],
        ["ResumeAiActorCommand"] = ["AiActor(ActorId):Write"],
        ["RunMcpToolCommand"] = ["Project(ProjectId):Query"],
        ["ScanAllRepositoriesCommand"] = ["Project(ProjectId):Write"],
        ["SearchProjectMemberCandidatesQuery"] = ["Project(ProjectId):ManageMembers"],
        ["SetProjectMemberCommand"] = ["Project(ProjectId):ManageMembers"],
        ["SetSubscriptionSlaCommand"] = ["Subscription(SubscriptionId):Write"],
        ["SetTaskPriorityCommand"] = ["Task(TaskId):Write"],
        ["SnoozeTaskCommand"] = ["Task(TaskId):Write"],
        ["StartDataSourceMetadataRefreshCommand"] = ["DataSource(DataSourceId):Manage:linking"],
        ["StartRepositoryScanCommand"] = ["Project(ProjectId):Write"],
        ["TestSubscriptionCommand"] = ["Subscription(SubscriptionId):Write"],
        ["ToggleQueryLockCommand"] = ["Query(QueryId):Manage"],
        ["UnwatchTaskCommand"] = ["Task(TaskId):Read"],
        ["UpdateDataContractCommand"] = ["DataContract(DataContractId):Write", "DataSource(DataSourceId):Write"],
        ["UpdateDocumentationSectionCommand"] = ["DocumentationSection(SectionId):Curate"],
        ["UpdatePatternStatusCommand"] = ["LearnedPattern(PatternId):Curate"],
        ["UpdateQueryCommand"] = ["Query(QueryId):Write"],
        ["UpdateSchemaRelationshipCommand"] = ["DataSource(DataSourceId):Write:linking"],
        ["VerifySchemaRelationshipCommand"] = ["DataSource(DataSourceId):Write:linking(Curate)"],
        ["WatchTaskCommand"] = ["Task(TaskId):Read"]
    };

    [Test]
    public void EveryAuthorizedRequest_DeclaresExactlyItsPinnedChecks()
    {
        var authorized = BeaconApiRoutes.RequestTypes()
            .Where(x => typeof(IProjectAuthorizedRequest).IsAssignableFrom(x))
            .ToList();

        authorized.Select(x => x.Name).Should().BeEquivalentTo(Manifest.Keys, "every authorized request is pinned, and nothing else");
        foreach (var request in authorized)
        {
            ChecksOf(request).Should().Equal(Manifest[request.Name], $"{request.Name} declares its pinned checks");
        }
    }

    [Test]
    public void APreviewOfAStoredQuery_Runs_AndOfADraft_ReadsTheQueryAndQueriesTheDraftsSources()
    {
        var draft = new QueryDraft { Steps = [Step(30), Step(31)] };

        Describe(new ExecuteQueryPreviewCommand { QueryId = 5 }).Should().Equal("Query(5):Run");
        Describe(new ExecuteQueryPreviewCommand { QueryId = 5, Draft = draft }).Should().Equal("Query(5):Read", "DataSources(30+31):Query");
        Describe(new ExecuteStepPreviewCommand { QueryId = 5, StepOrder = 1 }).Should().Equal("Query(5):Run");
        Describe(new ExecuteStepPreviewCommand { QueryId = 5, StepOrder = 1, Draft = draft }).Should().Equal("Query(5):Read", "DataSources(30+31):Query");
    }

    [Test]
    public void SavingAQueryWithSteps_WritesTheQueryAndItsStepsSources()
    {
        var withSteps = new UpdateQueryCommand { QueryId = 5, Query = new QueryData { Name = "q", Steps = [Step(31), Step(30)] } };
        var withoutSteps = new UpdateQueryCommand { QueryId = 5, Query = new QueryData { Name = "q", Steps = [] } };

        Describe(withSteps).Should().Equal("Query(5):Write", "DataSources(30+31):Write");
        Describe(withoutSteps).Should().Equal("Query(5):Write");
    }

    [Test]
    public void LockingAQuery_Writes_AndUnlockingItManages()
    {
        Describe(new ToggleQueryLockCommand { QueryId = 5, Lock = true }).Should().Equal("Query(5):Write");
        Describe(new ToggleQueryLockCommand { QueryId = 5, Lock = false }).Should().Equal("Query(5):Manage");
    }

    // Each id property gets its own sentinel, so a check is described by the property its id came from.
    private static IReadOnlyList<string> ChecksOf(Type request)
    {
        var instance = RuntimeHelpers.GetUninitializedObject(request);
        var names = new Dictionary<int, string>();
        var next = FirstSentinel;
        foreach (var property in request.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!IdProperty.IsMatch(property.Name) || property.SetMethod == null)
            {
                continue;
            }

            var sentinel = next++;
            names[sentinel] = property.Name;
            if (property.PropertyType == typeof(int) || property.PropertyType == typeof(int?))
            {
                property.SetValue(instance, sentinel);
            }
            else if (property.PropertyType == typeof(int[]))
            {
                property.SetValue(instance, new[] { sentinel });
            }
            else if (property.PropertyType.IsAssignableFrom(typeof(List<int>)))
            {
                property.SetValue(instance, new List<int> { sentinel });
            }
        }

        return ((IProjectAuthorizedRequest)instance).ProjectChecks
            .Select(x => Describe(x, y => names.TryGetValue(y, out var name) ? name : y.ToString()))
            .ToList();
    }

    private static IReadOnlyList<string> Describe(IProjectAuthorizedRequest request)
    {
        return request.ProjectChecks
            .Select(x => Describe(x, y => y.ToString()))
            .ToList();
    }

    private static string Describe(ProjectCheck check, Func<int, string> idName)
    {
        var ids = string.Join("+", check.Resource.Ids.Select(idName));
        var linking = (check.InEveryLinkingProject, check.EveryLinkingProjectAction) switch
        {
            (false, _) => string.Empty,
            (true, ProjectAction.Write) => ":linking",
            (true, var action) => $":linking({action})"
        };

        return $"{check.Resource.Kind}({ids}):{check.Action}{linking}";
    }

    private static QueryStepData Step(int dataSourceId)
    {
        return new QueryStepData { Name = "s", SqlValue = "select 1", DataSourceId = dataSourceId };
    }
}
