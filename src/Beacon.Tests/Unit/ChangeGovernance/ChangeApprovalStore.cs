using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// Migration jobs, data contracts with their rules, recipients and change requests behind recording contexts (§4.7: no
/// database, no in-memory provider): sets are served from these lists, conditional updates write onto them, transactions
/// are stand-ins the offline capture records, and every SaveChanges is counted and gives the entities it saved an id, as
/// the database would. <see cref="Commit"/> moves what was saved into the lists, as a commit makes it visible to the next
/// unit of work.
/// </summary>
internal sealed class ChangeApprovalStore
{
    public const int Warehouse = 3;
    public const int Ledger = 4;
    public const int Target = 5;

    private int _nextId = 100;

    public ChangeApprovalStore()
    {
        DataSources.Add(Source(Warehouse, "warehouse"));
        DataSources.Add(Source(Ledger, "ledger"));
        DataSources.Add(Source(Target, "target"));
    }

    /// <summary>Encrypts recipient destinations as the host does, with a throwaway key.</summary>
    public RecipientSecretProtector Protector { get; } = new(new EncryptionService(TestEncryptionKey.Value));

    public List<DataSource> DataSources { get; } = [];

    public List<MigrationJob> Jobs { get; } = [];

    public List<MigrationExecutionHistory> Executions { get; } = [];

    public List<DataContract> Contracts { get; } = [];

    public List<DataContractRule> Rules { get; } = [];

    public List<Recipient> Recipients { get; } = [];

    /// <summary>
    /// What reads of the contract rules see instead of <see cref="Rules"/> (bulk writes match through it too): a view for a
    /// test that changes the stored rules between two reads.
    /// </summary>
    public IEnumerable<DataContractRule>? RuleReads { get; set; }

    /// <summary>The stored rule results of past evaluations.</summary>
    public List<DataQualityRuleResult> Results { get; } = [];

    public List<ChangeApprovalRequest> Requests { get; } = [];

    public List<BeaconUser> Users { get; } = [];

    /// <summary>The warehouse's and the ledger's metadata: one orders table each, which contract rules may name.</summary>
    public List<DatabaseMetadata> Metadata { get; } =
    [
        new() { DataSourceId = Warehouse, SchemaName = "public", TableName = "orders", Columns = [new ColumnMetadata { ColumnName = "id", DataType = "int" }] },
        new() { DataSourceId = Ledger, SchemaName = "public", TableName = "orders", Columns = [new ColumnMetadata { ColumnName = "id", DataType = "int" }] }
    ];

    public List<object> Saved { get; } = [];

    public SqlCapture Offline { get; } = new();

    public int SaveCount { get; private set; }

    public IDbContextFactory<BeaconContext> Factory()
    {
        return Factory(trackContracts: false);
    }

    /// <summary>
    /// With <paramref name="trackContracts"/>, data contracts and their rules are added through EF's own change tracker (not
    /// served from the lists), so a save records every entity EF would insert with them — the recipients they reach
    /// included, unless those are tracked as unchanged. For a create, which reads no contract.
    /// </summary>
    public IDbContextFactory<BeaconContext> Factory(bool trackContracts)
    {
        var sets = new Dictionary<Type, object>
        {
            [typeof(DataSource)] = RecordingBeaconContext.MemorySet(DataSources, Saved),
            [typeof(MigrationJob)] = RecordingBeaconContext.MemorySet(Jobs, Saved),
            [typeof(MigrationExecutionHistory)] = RecordingBeaconContext.MemorySet(Executions, Saved),
            [typeof(Recipient)] = RecordingBeaconContext.MemorySet(Recipients, Saved),
            [typeof(ChangeApprovalRequest)] = RecordingBeaconContext.MemorySet(Requests, Saved),
            [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(Users, Saved),
            [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet(Metadata, Saved),
            [typeof(DataQualityRuleResult)] = RecordingBeaconContext.MemorySet(Results, Saved),
            [typeof(DataQualityScore)] = RecordingBeaconContext.MemorySet(new List<DataQualityScore>(), Saved),
        };
        if (!trackContracts)
        {
            sets[typeof(DataContract)] = RecordingBeaconContext.MemorySet(Contracts, Saved);
            sets[typeof(DataContractRule)] = RecordingBeaconContext.MemorySet(Rules, Saved, RuleReads);
        }

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, Saved, Offline, OnSave));

        return factory.Object;
    }

    /// <summary>Moves what was saved into the lists, as a commit makes it visible to the next unit of work.</summary>
    public void Commit()
    {
        foreach (var job in Saved.OfType<MigrationJob>().Where(x => !Jobs.Contains(x)))
        {
            Jobs.Add(job);
        }

        foreach (var recipient in Saved.OfType<Recipient>().Where(x => !Recipients.Contains(x)))
        {
            Recipients.Add(recipient);
        }

        foreach (var request in Saved.OfType<ChangeApprovalRequest>().Where(x => !Requests.Contains(x)))
        {
            Requests.Add(request);
        }

        foreach (var execution in Saved.OfType<MigrationExecutionHistory>().Where(x => !Executions.Contains(x)))
        {
            Executions.Add(execution);
        }

        foreach (var contract in Saved.OfType<DataContract>().Where(x => !Contracts.Contains(x)))
        {
            Contracts.Add(contract);
            Rules.AddRange(contract.Rules);
        }
    }

    public BeaconUser User(int id, string externalId, string? displayName = null)
    {
        var user = new BeaconUser { Id = id, ExternalId = externalId, UserName = externalId, DisplayName = displayName, IsEnabled = true };
        Users.Add(user);

        return user;
    }

    /// <summary>A stored migration job copying the warehouse into the target, its approved hash as given.</summary>
    public MigrationJob AddJob(string sql = "SELECT 1", string? approvedDefinitionHash = null)
    {
        var job = new MigrationJob
        {
            Id = _nextId++,
            Name = "copy orders",
            Description = "nightly copy",
            DataSourceId = Warehouse,
            DataSource = DataSources[0],
            QueryText = sql,
            DestinationDataSourceId = Target,
            DestinationDataSource = DataSources[2],
            DestinationTable = "orders",
            Mode = MigrationMode.Insert,
            ApprovedDefinitionHash = approvedDefinitionHash
        };
        Jobs.Add(job);

        return job;
    }

    /// <summary>A stored data contract on the warehouse holding <paramref name="rules"/>.</summary>
    public DataContract AddContract(params DataContractRule[] rules)
    {
        var contract = new DataContract
        {
            Id = _nextId++,
            DataSourceId = Warehouse,
            DataSource = DataSources[0],
            SchemaName = "public",
            TableName = "orders",
            Name = "orders contract",
            CronExpression = "0 * * * *",
            Rules = [.. rules]
        };
        foreach (var rule in rules)
        {
            rule.Id = _nextId++;
            rule.DataContractId = contract.Id;
            rule.DataContract = contract;
            Rules.Add(rule);
        }

        Contracts.Add(contract);

        return contract;
    }

    public static DataContractRule CustomSql(string sql, string name = "custom", string? approvedSqlHash = null) =>
        new()
        {
            Name = name,
            RuleType = DataContractRuleType.CustomSql,
            Configuration = $$"""{"sql":{{System.Text.Json.JsonSerializer.Serialize(sql)}}}""",
            IsEnabled = true,
            ApprovedSqlHash = approvedSqlHash
        };

    public static DataContractRule Volume(string name = "volume") =>
        new()
        {
            Name = name,
            RuleType = DataContractRuleType.Volume,
            Configuration = """{"minRows":1}""",
            IsEnabled = true
        };

    /// <summary>A stored recipient with an encrypted webhook destination, its approved hash as given.</summary>
    public Recipient AddRecipient(string destination = "https://hooks.example.com/a", string? approvedDestinationHash = null, string? headersJson = null)
    {
        var recipient = new Recipient
        {
            Id = _nextId++,
            Name = "ops hook",
            NotificationType = NotificationType.Webhook,
            Destination = Protector.Protect(destination),
            HeadersJson = Protector.ProtectOptional(headersJson),
            ApprovedDestinationHash = approvedDestinationHash
        };
        Recipients.Add(recipient);

        return recipient;
    }

    /// <summary>The hash of where a webhook to <paramref name="destination"/> delivers.</summary>
    public static string HookHash(string destination, string? headersJson = null) =>
        RecipientDestinationDefinition.Hash(TestChangeApprovals.Hasher, NotificationType.Webhook, destination, headersJson);

    public ChangeApprovalRequest AddRequest(
        ChangeApprovalKind kind,
        int resourceId,
        string definitionHash,
        string? requestedBy,
        ApprovalStatus status = ApprovalStatus.Pending,
        string proposedJson = "{}")
    {
        var request = new ChangeApprovalRequest
        {
            Id = _nextId++,
            Kind = kind,
            ResourceId = resourceId,
            ResourceName = "resource",
            Status = status,
            DefinitionHash = definitionHash,
            ProposedJson = proposedJson,
            PayloadHash = ChangeApprovalHashes.Sha256(proposedJson),
            RequestedByUserId = requestedBy
        };
        Requests.Add(request);

        return request;
    }

    private static DataSource Source(int id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "unused",
        };

    // A save gives the entities it saved an id, as the database would.
    private void OnSave()
    {
        SaveCount++;
        foreach (var entity in Saved.OfType<BaseEntity>().Where(x => x.Id == 0))
        {
            entity.Id = _nextId++;
        }

        // Rules are saved with their contract (a new one, or one whose rules a save rewrote): they get ids, and the stored
        // contracts' rules are the ones they now hold.
        foreach (var contract in Saved.OfType<DataContract>().Concat(Contracts))
        {
            foreach (var rule in contract.Rules.Where(x => x.Id == 0))
            {
                rule.Id = _nextId++;
                rule.DataContractId = contract.Id;
                rule.DataContract = contract;
            }
        }

        Rules.Clear();
        Rules.AddRange(Contracts.SelectMany(x => x.Rules));
    }
}
