using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.PostgreSql;
using Beacon.Core.SqlServer;
using Beacon.Core.Worker;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// The migration that stamps golden-case vectors indexed before they carried a project with their case's project, so
/// project-filtered retrieval finds them after an upgrade without re-embedding. Checked offline on both providers: it is
/// data only (its target model is the snapshot and it sorts last), and its SQL, run on in-memory SQLite, stamps only a
/// golden-case vector without a project whose case is on the vector's data source, leaving every other row as it was.
/// </summary>
[TestFixture]
public class GoldenCaseEmbeddingProjectBackfillTests
{
    private const string Suffix = "_BackfillGoldenCaseEmbeddingProjects";

    [TestCase(false)]
    [TestCase(true)]
    public void TheMigration_RunsOneBackfillStatement_ChangesNoSchema_AndSortsLast(bool sqlServer)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var differ = context.GetService<IMigrationsModelDiffer>();

        var migration = MigrationNamed(context, migrations, Suffix);

        var statement = migration.UpOperations.Should().ContainSingle().Which.Should().BeOfType<SqlOperation>().Subject;
        statement.Sql.Should().Contain(sqlServer ? "beacon.McpEmbeddings" : "beacon.mcp_embeddings");
        statement.Sql.Should().Contain($"= {(int)McpEmbeddingOwnerType.GoldenCase}");
        migration.DownOperations.Should().BeEmpty("which rows had no project before is not recorded");
        differ.GetDifferences(
                Initialized(migration.TargetModel!, initializer).GetRelationalModel(),
                Initialized(migrations.ModelSnapshot!.Model, initializer).GetRelationalModel())
            .Should().BeEmpty("the migration's target model is the snapshot");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        migrations.Migrations.Keys
            .Order(StringComparer.Ordinal)
            .Last()
            .Should().EndWith(Suffix, "it sorts after every earlier migration");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TheBackfill_StampsOnlyGoldenCaseVectorsWithoutAProject_WithTheirCasesProject(bool sqlServer)
    {
        var cases = new List<McpEvalCase>
        {
            Case(1, projectId: 1, dataSourceId: 7),
            Case(2, projectId: 2, dataSourceId: 7),
            Case(3, projectId: 1, dataSourceId: 8)
        };
        var embeddings = new List<McpEmbedding>
        {
            Embedding(11, McpEmbeddingOwnerType.GoldenCase, ownerId: 1, dataSourceId: 7, projectId: null),
            Embedding(12, McpEmbeddingOwnerType.GoldenCase, ownerId: 2, dataSourceId: 7, projectId: null),
            Embedding(13, McpEmbeddingOwnerType.GoldenCase, ownerId: 3, dataSourceId: 7, projectId: null),
            Embedding(14, McpEmbeddingOwnerType.GoldenCase, ownerId: 1, dataSourceId: 7, projectId: 5),
            Embedding(15, McpEmbeddingOwnerType.MetadataTable, ownerId: 1, dataSourceId: 7, projectId: null),
            Embedding(16, McpEmbeddingOwnerType.Exemplar, ownerId: 2, dataSourceId: 7, projectId: null),
            Embedding(17, McpEmbeddingOwnerType.GoldenCase, ownerId: 99, dataSourceId: 7, projectId: null)
        };

        var projects = RunBackfill(sqlServer, embeddings, cases);

        projects.Should().Equal(new Dictionary<int, int?>
        {
            [11] = 1,
            [12] = 2,
            [13] = null,
            [14] = 5,
            [15] = null,
            [16] = null,
            [17] = null
        });
    }

    /// <summary>
    /// Runs the migration's backfill statement on in-memory SQLite holding <paramref name="embeddings"/> and
    /// <paramref name="cases"/>, and returns each embedding's project afterwards by its id.
    /// </summary>
    internal static Dictionary<int, int?> RunBackfill(bool sqlServer, IEnumerable<McpEmbedding> embeddings, IEnumerable<McpEvalCase> cases)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "ATTACH DATABASE ':memory:' AS beacon;");
        Execute(
            connection,
            $"CREATE TABLE beacon.{Named(sqlServer, "mcp_embeddings")} ({Named(sqlServer, "id")} INTEGER PRIMARY KEY, {Named(sqlServer, "data_source_id")} INTEGER NULL, {Named(sqlServer, "project_id")} INTEGER NULL, {Named(sqlServer, "owner_type")} INTEGER NOT NULL, {Named(sqlServer, "owner_id")} INTEGER NOT NULL);");
        Execute(
            connection,
            $"CREATE TABLE beacon.{Named(sqlServer, "mcp_eval_cases")} ({Named(sqlServer, "id")} INTEGER PRIMARY KEY, {Named(sqlServer, "project_id")} INTEGER NOT NULL, {Named(sqlServer, "data_source_id")} INTEGER NOT NULL);");
        foreach (var embedding in embeddings)
        {
            Execute(
                connection,
                $"INSERT INTO beacon.{Named(sqlServer, "mcp_embeddings")} VALUES ($id, $dataSourceId, $projectId, $ownerType, $ownerId);",
                ("$id", embedding.Id),
                ("$dataSourceId", embedding.DataSourceId),
                ("$projectId", embedding.ProjectId),
                ("$ownerType", (int)embedding.OwnerType),
                ("$ownerId", embedding.OwnerId));
        }

        foreach (var evalCase in cases)
        {
            Execute(
                connection,
                $"INSERT INTO beacon.{Named(sqlServer, "mcp_eval_cases")} VALUES ($id, $projectId, $dataSourceId);",
                ("$id", evalCase.Id),
                ("$projectId", evalCase.ProjectId),
                ("$dataSourceId", evalCase.DataSourceId));
        }

        Execute(connection, BackfillSql(sqlServer));

        using var read = connection.CreateCommand();
        read.CommandText = $"SELECT {Named(sqlServer, "id")}, {Named(sqlServer, "project_id")} FROM beacon.{Named(sqlServer, "mcp_embeddings")} ORDER BY 1;";
        using var reader = read.ExecuteReader();
        var projects = new Dictionary<int, int?>();
        while (reader.Read())
        {
            projects[reader.GetInt32(0)] = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        }

        return projects;
    }

    private static string BackfillSql(bool sqlServer)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();

        return MigrationNamed(context, migrations, Suffix).UpOperations.OfType<SqlOperation>().Single().Sql;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    // SQL Server keeps the model's names (McpEmbeddings, OwnerId); PostgreSQL uses snake case.
    private static string Named(bool sqlServer, string snakeCase)
    {
        if (!sqlServer)
        {
            return snakeCase;
        }

        return string.Concat(snakeCase.Split('_').Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
    }

    private static McpEvalCase Case(int id, int projectId, int dataSourceId)
    {
        return new McpEvalCase
        {
            Id = id,
            ProjectId = projectId,
            DataSourceId = dataSourceId,
            Question = $"question {id}",
            GoldSql = "SELECT 1",
            IsActive = true
        };
    }

    private static McpEmbedding Embedding(int id, McpEmbeddingOwnerType ownerType, int ownerId, int dataSourceId, int? projectId)
    {
        return new McpEmbedding
        {
            Id = id,
            DataSourceId = dataSourceId,
            ProjectId = projectId,
            OwnerType = ownerType,
            OwnerId = ownerId,
            EmbeddingBytes = [],
            Model = "test"
        };
    }

    private static Migration MigrationNamed(BeaconContext context, IMigrationsAssembly migrations, string suffix)
    {
        var id = migrations.Migrations.Keys.Single(x => x.EndsWith(suffix, StringComparison.Ordinal));

        return migrations.CreateMigration(migrations.Migrations[id], context.Database.ProviderName!);
    }

    // A model built by a migration or a snapshot, made ready for the differ as the migrator makes it.
    private static IModel Initialized(IModel model, IModelRuntimeInitializer initializer)
    {
        var finalized = model is IMutableModel mutable ? mutable.FinalizeModel() : model;

        return initializer.Initialize(finalized, designTime: true, validationLogger: null);
    }

    private static ServiceProvider BuildProvider(bool sqlServer)
    {
        var services = new ServiceCollection();
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:EncryptionKey"] = TestEncryptionKey.Value })
            .Build();
        var builder = services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());
        if (sqlServer)
        {
            builder.UseSqlServer("Server=localhost;Database=unused;Trusted_Connection=True;TrustServerCertificate=True");
        }
        else
        {
            builder.UsePostgreSql("Host=localhost;Database=unused;Username=unused;Password=unused");
        }

        return services.BuildServiceProvider();
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
