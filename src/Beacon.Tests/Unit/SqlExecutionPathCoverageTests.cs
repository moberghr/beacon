using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A source scan of every place Beacon's own code can judge or send SQL to a data source (§1.5): the code that builds a
/// <c>SqlGateRequest</c> or calls <c>ISqlExecutionGate</c>, each call to a provider's execute, validate or cost-estimate
/// method (and the in-memory join's execute), the code that gets a data source's connection from
/// <c>IDataSourceConnectionResolver</c>, and the code that opens a driver connection through <c>DbConnectionFactory</c>.
/// A change to what a statement may read has to reach all of them, so the lists are exact: a new entry, a moved call or
/// one taken away fails until it is reviewed and the list says why. The scan pins where the calls are, and each reason
/// says what that call runs (the gate's own output); it also pins that only the gate places a row cap, so none is applied
/// after it, and where each gate request's row-level security context comes from: the resolver, or a listed file that
/// states one itself because what it judges reaches no source's rows.
/// </summary>
[TestFixture]
public class SqlExecutionPathCoverageTests
{
    // The production files that build a gate request or call the gate, and why.
    private static readonly Dictionary<string, string> GateUsers = new(StringComparer.Ordinal)
    {
        ["src/Beacon.Core/Services/Validation/ISqlExecutionGate.cs"] = "the gate's contract and its request",
        ["src/Beacon.Core/Services/Validation/SqlExecutionGate.cs"] = "the gate",
        ["src/Beacon.Core/ServiceConfiguration.cs"] = "registers the gate",
        ["src/Beacon.Core/Services/GatedSqlRunner.cs"] = "gates and runs previews, query steps, subscriptions, AI actor checks, migration reads and column value samples",
        ["src/Beacon.Core/Services/QueryService.cs"] = "gates the final query of a multi-step run on the in-memory join",
        ["src/Beacon.Core/Services/StepSqlSaveCheck.cs"] = "judges a query step's SQL before it is stored, runs nothing",
        ["src/Beacon.Core/Services/DataQualityRuleGuard.cs"] = "builds a Custom SQL rule's gate request and judges the rule when a contract is saved",
        ["src/Beacon.Core/Handlers/DataQuality/CreateDataContractHandler.cs"] = "hands the gate to DataQualityRuleGuard on create, runs nothing",
        ["src/Beacon.Core/Handlers/DataQuality/UpdateDataContractHandler.cs"] = "hands the gate to DataQualityRuleGuard on update, runs nothing",
        ["src/Beacon.Core/Services/DataQualityEvaluationService.cs"] = "gates a Custom SQL rule before it runs",
        ["src/Beacon.Core/SavedQueries/SavedQueryToolExecutor.cs"] = "gates a saved query's steps and its in-memory join before they run",
        ["src/Beacon.AI/Services/Mcp/AskSqlPipeline.cs"] = "judges each ask candidate and repair; the executor gates the statement it runs",
        ["src/Beacon.AI/Services/Documentation/ProjectDocumentationService.cs"] = "gates enum discovery statements",
        ["src/Beacon.AI/Services/Knowledge/ValueGroundingService.cs"] = "gates value-grounding probes",
        ["src/Beacon.AI/Services/Eval/EvalReadOnlySqlExecutor.cs"] = "gates the eval harness's statements",
        ["src/Beacon.AI/Services/Eval/McpEvalService.cs"] = "hands the gate to the eval executor",
        ["src/Beacon.MCP/Services/QueryExecutionService.cs"] = "gates the ask statement it runs",
        ["src/Beacon.MCP/Services/CrossSourceQueryService.cs"] = "gates each source's statement, its retries and the in-memory join",
        ["src/Beacon.MCP/Tools/ProjectQueryTool.cs"] = "gates the query tool's statement",
        ["src/Beacon.MCP/Tools/DryRunTool.cs"] = "judges a dry_run statement before the provider validates it"
    };

    // The production files that name the row-security resolver, and why: each resolves the context of what it gates for the
    // scope's caller.
    private static readonly Dictionary<string, string> RowSecurityResolverUsers = new(StringComparer.Ordinal)
    {
        ["src/Beacon.Core/RowSecurity/IRowSecurityResolver.cs"] = "the resolver's contract",
        ["src/Beacon.Core/RowSecurity/RowSecurityResolver.cs"] = "the resolver",
        ["src/Beacon.Core/ServiceConfiguration.cs"] = "registers the resolver",
        ["src/Beacon.Core/Services/QueryService.cs"] = "query steps, step previews and a proposal's validation on its sources",
        ["src/Beacon.Core/Services/Metadata/ColumnValueSampler.cs"] = "column value samples",
        ["src/Beacon.Core/Services/DataQualityEvaluationService.cs"] = "a contract's rules",
        ["src/Beacon.Core/SavedQueries/SavedQueryToolExecutor.cs"] = "a saved query's steps",
        ["src/Beacon.AI/Services/Mcp/AskSqlPipeline.cs"] = "ask candidates and repairs",
        ["src/Beacon.AI/Services/Documentation/ProjectDocumentationService.cs"] = "enum discovery statements",
        ["src/Beacon.AI/Services/Knowledge/ValueGroundingService.cs"] = "value-grounding probes",
        ["src/Beacon.AI/Services/Eval/EvalReadOnlySqlExecutor.cs"] = "the eval harness's statements",
        ["src/Beacon.AI/Services/Eval/McpEvalService.cs"] = "hands the resolver to the eval executor",
        ["src/Beacon.MCP/Services/QueryExecutionService.cs"] = "the ask statement it runs",
        ["src/Beacon.MCP/Services/CrossSourceQueryService.cs"] = "each source's statement and its retries",
        ["src/Beacon.MCP/Tools/ProjectQueryTool.cs"] = "the query tool's statement",
        ["src/Beacon.MCP/Tools/DryRunTool.cs"] = "a dry_run statement"
    };

    // The production files that create a row-security context, and why. What runs on a source takes its context from the
    // resolver; anything else stated here reaches no source's rows.
    private static readonly Dictionary<string, string> RowSecurityContextCreators = new(StringComparer.Ordinal)
    {
        ["src/Beacon.Core/RowSecurity/RowSecurityResolver.cs"] = "the resolver",
        ["src/Beacon.Core/Services/Validation/ISqlExecutionGate.cs"] = "the in-memory join's request: it reads only step results, each resolved for its own source",
        ["src/Beacon.Core/Services/QueryService.cs"] = "a multi-step run's in-memory join: it reads only step results, each resolved for its own source",
        ["src/Beacon.Core/Services/StepSqlSaveCheck.cs"] = "judges a query step's SQL before it is stored, runs nothing",
        ["src/Beacon.Core/Services/DataQualityRuleGuard.cs"] = "judges a Custom SQL rule when its contract is saved, runs nothing"
    };

    // The gate users that neither resolve nor create a context, and why.
    private static readonly Dictionary<string, string> RowSecurityContextReceivers = new(StringComparer.Ordinal)
    {
        ["src/Beacon.Core/Services/Validation/SqlExecutionGate.cs"] = "the gate: acts on the request's context",
        ["src/Beacon.Core/Services/GatedSqlRunner.cs"] = "takes the context in its request from its callers",
        ["src/Beacon.Core/Handlers/DataQuality/CreateDataContractHandler.cs"] = "hands the gate to DataQualityRuleGuard, runs nothing",
        ["src/Beacon.Core/Handlers/DataQuality/UpdateDataContractHandler.cs"] = "hands the gate to DataQualityRuleGuard, runs nothing"
    };

    // Per production file: how many calls to a provider's execute, validate or cost-estimate method (or the in-memory
    // join's execute) it makes, and what each one runs.
    private static readonly Dictionary<string, (int Calls, string Reason)> ExecuteCallers = new(StringComparer.Ordinal)
    {
        ["src/Beacon.Core/Services/GatedSqlRunner.cs"] = (3, "the row-capped and the plain read of the gate's FinalSql, and the non-executing validation after the gate's verdict"),
        ["src/Beacon.Connector.BigQuery/BigQueryProvider.cs"] = (1, "declares the row-capped read the runner calls (the gate's FinalSql), not a call of its own"),
        ["src/Beacon.Connector.Databricks/DatabricksProvider.cs"] = (1, "declares the row-capped read the runner calls (the gate's FinalSql), not a call of its own"),
        ["src/Beacon.Core/Services/QueryCostLimit.cs"] = (1, "the planner's estimate (EXPLAIN without ANALYZE) of a statement its callers gated"),
        ["src/Beacon.Core/Services/VirtualTableManager.cs"] = (1, "the in-memory join on Beacon's own SQLite store, of the SQL its caller's gate wrote"),
        ["src/Beacon.Core/Services/DataQualityEvaluationService.cs"] = (1, "a Custom SQL rule's FinalSql"),
        ["src/Beacon.Core/SavedQueries/SavedQueryToolExecutor.cs"] = (2, "a saved query step's FinalSql, and its in-memory join's FinalSql"),
        ["src/Beacon.AI/Services/Documentation/ProjectDocumentationService.cs"] = (1, "an enum discovery statement's FinalSql"),
        ["src/Beacon.AI/Services/Knowledge/ValueGroundingService.cs"] = (1, "a value-grounding probe's FinalSql"),
        ["src/Beacon.AI/Services/Eval/EvalReadOnlySqlExecutor.cs"] = (1, "an eval statement's FinalSql"),
        ["src/Beacon.MCP/Services/QueryExecutionService.cs"] = (2, "the ask statement's FinalSql, and the dry run of a statement its callers gated"),
        ["src/Beacon.MCP/Services/CrossSourceQueryService.cs"] = (3, "each source's FinalSql, the in-memory join's FinalSql, and the dry run of a gated statement or retry"),
        ["src/Beacon.MCP/Tools/ProjectQueryTool.cs"] = (2, "the query tool's FinalSql, and an API source's request (no SQL)")
    };

    // The production files that get a data source's connection (or read what it holds) from the resolver, and why.
    private static readonly Dictionary<string, string> ResolverUsers = new(StringComparer.Ordinal)
    {
        ["src/Beacon.Core/HostData/DataSourceConnectionResolver.cs"] = "the resolver",
        ["src/Beacon.Core/ServiceConfiguration.cs"] = "registers the resolver",
        ["src/Beacon.Core/Services/Providers/DatabaseProvider.cs"] = "the database connectors' reads",
        ["src/Beacon.Connector.Api/ApiProvider.cs"] = "the API connector's requests",
        ["src/Beacon.Connector.BigQuery/BigQueryProvider.cs"] = "the BigQuery connector's reads",
        ["src/Beacon.Connector.CloudWatch/CloudWatchProvider.cs"] = "the CloudWatch connector's reads",
        ["src/Beacon.Connector.Databricks/DatabricksProvider.cs"] = "the Databricks connector's reads",
        ["src/Beacon.Core/Services/DatabaseMetadataService.cs"] = "the metadata scan's fixed catalog SQL",
        ["src/Beacon.Core/Services/MigrationService.cs"] = "the migration writer's connection to its write target",
        ["src/Beacon.Core/Services/Providers/DataSourceTransportStatus.cs"] = "the transport badge, connects to nothing",
        ["src/Beacon.Core/Services/Validation/DataSourceConfiguredDatabase.cs"] = "reads the configured database's name, connects to nothing",
        ["src/Beacon.Core/HostData/HostReadOnlyLoginCheck.cs"] = "the startup check of a host source's read-only login: fixed catalog SQL, outside the gate",
        ["src/Beacon.Api/ApprovalWorkflowStartupNote.cs"] = "the startup warning that the registered resolver is not Beacon's, connects to nothing"
    };

    // The production files that name DbConnectionFactory (a driver connection from a connection string), and why.
    private static readonly Dictionary<string, string> ConnectionFactoryUsers = new(StringComparer.Ordinal)
    {
        ["src/Beacon.Core/Helpers/DbConnectionFactory.cs"] = "the factory",
        ["src/Beacon.Connector.PostgreSql/ServiceCollectionExtensions.cs"] = "registers the PostgreSQL driver",
        ["src/Beacon.Connector.MySql/ServiceCollectionExtensions.cs"] = "registers the MySQL driver",
        ["src/Beacon.Connector.SqlServer/ServiceCollectionExtensions.cs"] = "registers the SQL Server driver",
        ["src/Beacon.Connector.AzureSynapse/ServiceCollectionExtensions.cs"] = "registers the Azure Synapse driver",
        ["src/Beacon.Connector.Snowflake/ServiceCollectionExtensions.cs"] = "registers the Snowflake driver",
        ["src/Beacon.Core/Services/Providers/DatabaseProvider.cs"] = "the database connectors' reads, on the resolver's connection",
        ["src/Beacon.Connector.PostgreSql/PostgreSqlMetadataExtractor.cs"] = "the metadata scan's fixed catalog SQL, on the connection the scan resolved",
        ["src/Beacon.Connector.MySql/MySqlMetadataExtractor.cs"] = "the metadata scan's fixed catalog SQL, on the connection the scan resolved",
        ["src/Beacon.Connector.AzureSynapse/AzureSynapseMetadataExtractor.cs"] = "the metadata scan's fixed catalog SQL, on the connection the scan resolved",
        ["src/Beacon.Core/Services/MigrationService.Sql.cs"] = "the migration writer's connection to its write target, from the resolver",
        ["src/Beacon.Core/Services/DataSourceService.cs"] = "tests a new connection after its transport check (the engine verifier identifies the engine on it) and empties a replaced or refused connection's pool",
        ["src/Beacon.Core/Services/Providers/DatabaseEngineVerifier.cs"] = "identifies a new connection's engine on create and update: one fixed catalog statement, outside the gate; empties a refused connection's pool",
        ["src/Beacon.Core/HostData/HostReadOnlyLoginCheck.cs"] = "the startup check of a host source's read-only login, on the resolver's connection: fixed catalog SQL, outside the gate"
    };

    private static readonly Regex GateUse = new(@"\b(?:ISqlExecutionGate|SqlGateRequest)\b", RegexOptions.CultureInvariant);

    private static readonly Regex ExecuteCall = new(@"\.(?:ExecuteReadOnlyQueryAsync|ExecuteQueryAsync|ValidateQueryAsync|EstimateReadOnlyCostAsync)\b", RegexOptions.CultureInvariant);

    private static readonly Regex ResolverUse = new(@"\bIDataSourceConnectionResolver\b", RegexOptions.CultureInvariant);

    private static readonly Regex ConnectionFactoryUse = new(@"\bDbConnectionFactory\b", RegexOptions.CultureInvariant);

    private static readonly Regex RowLimitRewrite = new(@"\bSqlRowLimitRewriter\.Apply\b", RegexOptions.CultureInvariant);

    private static readonly Regex RowSecurityResolverUse = new(@"\bIRowSecurityResolver\b", RegexOptions.CultureInvariant);

    private static readonly Regex RowSecurityContextCreation = new(@"\bRowSecurityContext\.(?:NotApplicable|Filtered|Denied)\(", RegexOptions.CultureInvariant);

    private static readonly Lazy<Dictionary<string, string>> Sources = new(ReadProductionSources);

    [Test]
    public void EveryFileThatUsesTheGate_IsListed()
    {
        var users = Sources.Value
            .Where(x => GateUse.IsMatch(x.Value))
            .Select(x => x.Key)
            .ToList();

        users.Should().BeEquivalentTo(GateUsers.Keys, "a new gate user must be reviewed and listed with its reason, and a removed one taken off the list");
    }

    [Test]
    public void EveryProviderExecuteCall_IsListed_WithItsCount()
    {
        var calls = Sources.Value
            .Select(x => new { File = x.Key, Calls = ExecuteCall.Matches(x.Value).Count })
            .Where(x => x.Calls > 0)
            .ToDictionary(x => x.File, x => x.Calls, StringComparer.Ordinal);

        calls.Should().BeEquivalentTo(
            ExecuteCallers.ToDictionary(x => x.Key, x => x.Value.Calls, StringComparer.Ordinal),
            "every statement that reaches a data source must be the gate's output: a new call site is reviewed and listed with what it runs");
    }

    [Test]
    public void EveryFileThatUsesTheConnectionResolver_IsListed()
    {
        var users = Sources.Value
            .Where(x => ResolverUse.IsMatch(x.Value))
            .Select(x => x.Key)
            .ToList();

        users.Should().BeEquivalentTo(ResolverUsers.Keys, "a connection obtained outside the providers could run SQL the gate never saw");
    }

    [Test]
    public void EveryFileThatOpensADriverConnection_IsListed()
    {
        var users = Sources.Value
            .Where(x => ConnectionFactoryUse.IsMatch(x.Value))
            .Select(x => x.Key)
            .ToList();

        users.Should().BeEquivalentTo(ConnectionFactoryUsers.Keys, "a driver connection opened outside the providers could run SQL the gate never saw");
    }

    [Test]
    public void OnlyTheGate_PlacesARowCap()
    {
        var callers = Sources.Value
            .Where(x => RowLimitRewrite.IsMatch(x.Value))
            .Select(x => x.Key)
            .ToList();

        callers.Should().Equal(
            ["src/Beacon.Core/Services/Validation/SqlExecutionGate.cs"],
            "the row cap is the gate's own stage (SqlGateRequest.MaxRows), so what runs is the gate's FinalSql and no limit is applied after it");
    }

    [Test]
    public void EveryFileThatUsesTheRowSecurityResolver_IsListed()
    {
        var users = Sources.Value
            .Where(x => RowSecurityResolverUse.IsMatch(x.Value))
            .Select(x => x.Key)
            .ToList();

        users.Should().BeEquivalentTo(RowSecurityResolverUsers.Keys, "a new path that resolves row security must be reviewed and listed, and a removed one taken off the list");
    }

    [Test]
    public void EveryFileThatCreatesARowSecurityContext_IsListed()
    {
        var creators = Sources.Value
            .Where(x => RowSecurityContextCreation.IsMatch(x.Value))
            .Select(x => x.Key)
            .ToList();

        creators.Should().BeEquivalentTo(
            RowSecurityContextCreators.Keys,
            "a context stated anywhere but the resolver could let a statement read rows its caller may not: only what reaches no source's rows states its own");
    }

    [Test]
    public void EveryGateUser_TakesItsRowSecurityContextFromAListedPlace()
    {
        var others = GateUsers.Keys
            .Except(RowSecurityResolverUsers.Keys)
            .Except(RowSecurityContextCreators.Keys)
            .ToList();

        others.Should().BeEquivalentTo(RowSecurityContextReceivers.Keys, "every gate request carries a context: from the resolver, created by a listed file, or handed in by a caller");
    }

    // Production C# under src, without tests, migrations and build output, each file without its whole-line comments so a
    // doc comment that names a type is not a use of it.
    private static Dictionary<string, string> ReadProductionSources()
    {
        var root = RepositoryRoot();

        return Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(x => Path.GetRelativePath(root, x).Replace('\\', '/'))
            .Where(x => !x.StartsWith("src/Beacon.Tests/", StringComparison.Ordinal))
            .Where(x => !x.Contains("/Migrations/", StringComparison.Ordinal))
            .Where(x => !x.Contains("/obj/", StringComparison.Ordinal))
            .Where(x => !x.Contains("/bin/", StringComparison.Ordinal))
            .ToDictionary(x => x, x => WithoutLineComments(File.ReadAllText(Path.Combine(root, x))), StringComparer.Ordinal);
    }

    private static string WithoutLineComments(string text)
    {
        return string.Join('\n', text
            .Split('\n')
            .Where(x => !x.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Beacon.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests run inside the repository");

        return directory!.FullName;
    }
}
