using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.DbtIntegration;

internal sealed class DbtIntegrationService(
    IDbContextFactory<BeaconContext> contextFactory,
    ILogger<DbtIntegrationService> logger) : IDbtIntegrationService
{
    public Task<DbtManifest> ParseManifestAsync(string manifestJson, CancellationToken ct = default)
    {
        var manifest = new DbtManifest();
        var models = new List<DbtModel>();
        var sources = new List<DbtSource>();
        var tests = new List<DbtTest>();

        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            var root = doc.RootElement;

            // Parse nodes (models)
            if (root.TryGetProperty("nodes", out var nodes))
            {
                foreach (var node in nodes.EnumerateObject())
                {
                    if (!node.Value.TryGetProperty("resource_type", out var resourceType))
                        continue;

                    var type = resourceType.GetString();
                    if (type == "model")
                    {
                        var name = node.Value.GetProperty("name").GetString() ?? "";
                        var schema = node.Value.TryGetProperty("schema", out var s) ? s.GetString() : null;
                        var description = node.Value.TryGetProperty("description", out var d) ? d.GetString() : null;
                        var materialized = node.Value.TryGetProperty("config", out var config) && config.TryGetProperty("materialized", out var mat)
                            ? mat.GetString() : null;

                        var columns = ParseColumns(node.Value);
                        var dependsOn = ParseDependsOn(node.Value);

                        models.Add(new DbtModel(name, schema, description, materialized, columns, dependsOn));
                    }
                    else if (type == "test")
                    {
                        var testName = node.Value.GetProperty("name").GetString() ?? "";
                        var testType = node.Value.TryGetProperty("test_metadata", out var meta) && meta.TryGetProperty("name", out var tn)
                            ? tn.GetString() ?? "custom" : "custom";

                        string? modelName = null;
                        string? columnName = null;

                        if (node.Value.TryGetProperty("test_metadata", out var testMeta))
                        {
                            if (testMeta.TryGetProperty("kwargs", out var kwargs))
                            {
                                if (kwargs.TryGetProperty("model", out var m)) modelName = m.GetString();
                                if (kwargs.TryGetProperty("column_name", out var c)) columnName = c.GetString();
                            }
                        }

                        tests.Add(new DbtTest(testName, modelName, columnName, testType));
                    }
                }
            }

            // Parse sources
            if (root.TryGetProperty("sources", out var sourcesNode))
            {
                foreach (var source in sourcesNode.EnumerateObject())
                {
                    var name = source.Value.GetProperty("name").GetString() ?? "";
                    var schema = source.Value.TryGetProperty("schema", out var s) ? s.GetString() : null;
                    var description = source.Value.TryGetProperty("description", out var d) ? d.GetString() : null;
                    var columns = ParseColumns(source.Value);

                    sources.Add(new DbtSource(name, schema, description, columns));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse dbt manifest");
            throw;
        }

        return Task.FromResult(new DbtManifest { Models = models, Sources = sources, Tests = tests });
    }

    public async Task<int> ImportModelsAsync(int projectId, int dataSourceId, DbtManifest manifest, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Find or create a placeholder GitHub repo for dbt references
        var repo = await context.GitHubRepositories
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.RepositoryUrl.Contains("dbt://"), ct);

        if (repo == null)
        {
            repo = new GitHubRepository
            {
                ProjectId = projectId,
                RepositoryUrl = "dbt://imported",
                Branch = "main",
                ScanStatus = ScanStatus.Completed,
                LastScanAt = DateTime.UtcNow
            };
            context.GitHubRepositories.Add(repo);
            await context.SaveChangesAsync(ct);
        }

        // Clear old dbt references
        var oldRefs = await context.CodeReferences
            .Where(r => r.GitHubRepositoryId == repo.Id)
            .ToListAsync(ct);
        context.CodeReferences.RemoveRange(oldRefs);

        var referencesCreated = 0;

        // Import models as code references
        foreach (var model in manifest.Models)
        {
            var codeRef = new CodeReference
            {
                GitHubRepositoryId = repo.Id,
                FilePath = $"models/{model.Name}.sql",
                ReferenceType = CodeReferenceType.RawSql,
                SchemaName = model.Schema,
                TableName = model.Name,
                ClassName = "dbt",
                MethodName = model.MaterializedAs ?? "view",
                CodeSnippet = model.Description
            };
            context.CodeReferences.Add(codeRef);
            referencesCreated++;

            // Import column descriptions into metadata if they exist
            foreach (var col in model.Columns)
            {
                if (col.Description == null) continue;

                var metadata = await context.DatabaseMetadata
                    .Include(m => m.Columns)
                    .FirstOrDefaultAsync(m => m.DataSourceId == dataSourceId && m.TableName == model.Name, ct);

                if (metadata != null)
                {
                    var colMeta = metadata.Columns.FirstOrDefault(c =>
                        c.ColumnName.Equals(col.Name, StringComparison.OrdinalIgnoreCase));
                    if (colMeta != null)
                        colMeta.Description = col.Description;
                }
            }
        }

        // Import sources
        foreach (var source in manifest.Sources)
        {
            var codeRef = new CodeReference
            {
                GitHubRepositoryId = repo.Id,
                FilePath = $"sources/{source.Name}.yml",
                ReferenceType = CodeReferenceType.DbContextConfiguration,
                SchemaName = source.Schema,
                TableName = source.Name,
                ClassName = "dbt-source",
                CodeSnippet = source.Description
            };
            context.CodeReferences.Add(codeRef);
            referencesCreated++;
        }

        repo.TotalReferencesFound = referencesCreated;
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Imported {Count} dbt references for project {ProjectId}", referencesCreated, projectId);
        return referencesCreated;
    }

    private static List<DbtColumn> ParseColumns(JsonElement node)
    {
        var columns = new List<DbtColumn>();
        if (!node.TryGetProperty("columns", out var colsNode)) return columns;

        foreach (var col in colsNode.EnumerateObject())
        {
            var name = col.Value.TryGetProperty("name", out var n) ? n.GetString() ?? col.Name : col.Name;
            var description = col.Value.TryGetProperty("description", out var d) ? d.GetString() : null;
            var testList = new List<string>();

            if (col.Value.TryGetProperty("tests", out var testsArr) && testsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var test in testsArr.EnumerateArray())
                {
                    if (test.ValueKind == JsonValueKind.String)
                        testList.Add(test.GetString()!);
                    else if (test.ValueKind == JsonValueKind.Object)
                        testList.Add(test.EnumerateObject().FirstOrDefault().Name);
                }
            }

            columns.Add(new DbtColumn(name, description, testList));
        }

        return columns;
    }

    private static List<string> ParseDependsOn(JsonElement node)
    {
        var deps = new List<string>();
        if (!node.TryGetProperty("depends_on", out var depsNode)) return deps;
        if (!depsNode.TryGetProperty("nodes", out var nodesArr)) return deps;

        foreach (var dep in nodesArr.EnumerateArray())
        {
            var depStr = dep.GetString();
            if (depStr != null) deps.Add(depStr);
        }

        return deps;
    }
}
