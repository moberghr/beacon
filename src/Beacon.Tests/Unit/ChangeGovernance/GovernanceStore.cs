using System.Text.Json;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// One stored query with one step on the warehouse, its versions, approval requests, subscriptions, the users who work on
/// it and AI actor plans, behind recording contexts (§4.7: no database, no in-memory provider): sets are served from these
/// lists, conditional updates write onto them, transactions are stand-ins the offline capture records, and every
/// SaveChanges is counted. <see cref="Commit"/> moves what a submission saved into the lists, as a commit makes it visible
/// to the next unit of work.
/// </summary>
internal sealed class GovernanceStore
{
    public const int QueryId = 7;
    public const int StepId = 11;
    public const int DataSourceId = 3;
    public const int LedgerId = 4;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private int _nextId = 100;

    public GovernanceStore()
    {
        Query = new Query
        {
            Id = QueryId,
            Name = "Orders",
            Steps =
            [
                new QueryStep
                {
                    Id = StepId,
                    QueryId = QueryId,
                    DataSourceId = DataSourceId,
                    DataSource = Warehouse,
                    StepOrder = 1,
                    Name = "Step 1",
                    SqlValue = "SELECT 1",
                },
            ],
        };
        Queries.Add(Query);
    }

    public DataSource Warehouse { get; } = new()
    {
        Id = DataSourceId,
        Name = "warehouse",
        DataSourceType = DataSourceType.Database,
        DatabaseEngineType = DatabaseEngineType.PostgreSQL,
        EncryptedConnectionData = "unused",
    };

    public DataSource Ledger { get; } = new()
    {
        Id = LedgerId,
        Name = "ledger",
        DataSourceType = DataSourceType.Database,
        DatabaseEngineType = DatabaseEngineType.PostgreSQL,
        EncryptedConnectionData = "unused",
    };

    public Query Query { get; }

    public List<Query> Queries { get; } = [];

    public List<QueryVersion> Versions { get; } = [];

    public List<QueryApprovalRequest> Requests { get; } = [];

    public List<Subscription> Subscriptions { get; } = [];

    public List<BeaconUser> Users { get; } = [];

    public List<AiActorPlan> Plans { get; } = [];

    public List<object> Saved { get; } = [];

    public SqlCapture Offline { get; } = new();

    public int SaveCount { get; private set; }

    public IDbContextFactory<BeaconContext> Factory()
    {
        var sets = new Dictionary<Type, object>
        {
            [typeof(Query)] = RecordingBeaconContext.MemorySet(Queries, Saved),
            [typeof(QueryStep)] = RecordingBeaconContext.MemorySet(Query.Steps, Saved),
            [typeof(QueryStepParameter)] = RecordingBeaconContext.MemorySet(new List<QueryStepParameter>(), Saved),
            [typeof(DataSource)] = RecordingBeaconContext.MemorySet<DataSource>([Warehouse, Ledger], Saved),
            [typeof(QueryVersion)] = RecordingBeaconContext.MemorySet(Versions, Saved),
            [typeof(QueryApprovalRequest)] = RecordingBeaconContext.MemorySet(Requests, Saved),
            [typeof(Subscription)] = RecordingBeaconContext.MemorySet(Subscriptions, Saved),
            [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(Users, Saved),
            [typeof(AiActorPlan)] = RecordingBeaconContext.MemorySet(Plans, Saved),
            [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet(new List<DatabaseMetadata>(), Saved),
        };

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, Saved, Offline, () => SaveCount++));

        return factory.Object;
    }

    /// <summary>
    /// The versions and requests saved so far, given ids and moved into <see cref="Versions"/> and <see cref="Requests"/>:
    /// what the next unit of work reads once the one that saved them committed.
    /// </summary>
    public void Commit()
    {
        foreach (var version in Saved.OfType<QueryVersion>().Where(x => !Versions.Contains(x)))
        {
            version.Id = _nextId++;
            Versions.Add(version);
        }

        foreach (var request in Saved.OfType<QueryApprovalRequest>().Where(x => !Requests.Contains(x)))
        {
            request.Id = _nextId++;
            request.QueryVersionId = request.QueryVersion.Id;
            Requests.Add(request);
        }
    }

    public BeaconUser User(int id, string externalId, string? displayName)
    {
        var user = new BeaconUser { Id = id, ExternalId = externalId, UserName = externalId, DisplayName = displayName, IsEnabled = true };
        Users.Add(user);

        return user;
    }

    /// <summary>A version of the query holding one warehouse step with <paramref name="sql"/>, its hash recorded unless <paramref name="legacy"/>.</summary>
    public QueryVersion AddVersion(int number, QueryVersionStatus status, string sql = "SELECT 1", bool legacy = false, string? finalQuery = null)
    {
        var steps = new List<QueryStepSnapshot> { Snapshot(sql) };
        var version = new QueryVersion
        {
            Id = _nextId++,
            QueryId = QueryId,
            Query = Query,
            VersionNumber = number,
            Status = status,
            Name = "Orders",
            FinalQuery = finalQuery,
            StepsJson = JsonSerializer.Serialize(steps, JsonOptions),
            DataSourceIds = [DataSourceId],
            ContentHash = legacy ? null : QueryContentHash.Of(finalQuery, steps).Hash,
        };
        Versions.Add(version);

        return version;
    }

    /// <summary>Makes <paramref name="version"/> the query's active version.</summary>
    public QueryVersion Activate(QueryVersion version)
    {
        version.Status = QueryVersionStatus.Active;
        Query.ActiveVersionId = version.Id;
        Query.ActiveVersion = version;

        return version;
    }

    public QueryApprovalRequest AddRequest(
        QueryVersion version,
        string? requestedBy,
        int? baseVersionId,
        ApprovalStatus status = ApprovalStatus.Pending,
        string? approvedContentHash = null,
        string? reviewedBy = null)
    {
        var request = new QueryApprovalRequest
        {
            Id = _nextId++,
            QueryId = QueryId,
            Query = Query,
            QueryVersionId = version.Id,
            QueryVersion = version,
            Status = status,
            RequestedByUserId = requestedBy,
            BaseVersionId = baseVersionId,
            ApprovedContentHash = approvedContentHash,
            ReviewedByUserId = reviewedBy,
        };
        Requests.Add(request);

        return request;
    }

    public static QueryStepSnapshot Snapshot(string sql, int order = 1, string name = "Step 1") =>
        new()
        {
            StepOrder = order,
            SqlValue = sql,
            DataSourceId = DataSourceId,
            DataSourceName = "warehouse",
            Name = name,
        };

    /// <summary>The query as an edit sends it: one step (the stored one), with <paramref name="sql"/>.</summary>
    public static QueryData Edit(string sql, string? finalQuery = null) =>
        new()
        {
            QueryId = QueryId,
            Name = "Orders",
            FinalQuery = finalQuery,
            Steps =
            [
                new QueryStepData
                {
                    StepId = StepId,
                    StepOrder = 1,
                    Name = "Step 1",
                    SqlValue = sql,
                    DataSourceId = DataSourceId,
                },
            ],
        };
}
