using System.ComponentModel;
using System.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Services.Ai.DocumentationAgent.Models;

namespace Semantico.Core.Services.Ai.DocumentationAgent;

/// <summary>
/// Defines the tools available to the Documentation Agent for analyzing and documenting database schemas.
/// These tools wrap existing Semantico services and provide structured interfaces for the AI agent.
/// </summary>
public class DocumentationAgentTools
{
    private readonly IDatabaseMetadataService _metadataService;
    private readonly IDbContextFactory<SemanticoContext> _contextFactory;
    private readonly IEncryptionService _encryptionService;
    private readonly ILogger<DocumentationAgentTools> _logger;

    public DocumentationAgentTools(
        IDatabaseMetadataService metadataService,
        IDbContextFactory<SemanticoContext> contextFactory,
        IEncryptionService encryptionService,
        ILogger<DocumentationAgentTools> logger)
    {
        _metadataService = metadataService;
        _contextFactory = contextFactory;
        _encryptionService = encryptionService;
        _logger = logger;
    }

    [Description("Get list of all tables in the data source schema with their basic metadata.")]
    public async Task<TableListResult> GetTableList(
        [Description("The data source ID to query")] int dataSourceId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("GetTableList called for DataSource {DataSourceId}", dataSourceId);

        var metadata = await _metadataService.GetMetadataAsync(dataSourceId, cancellationToken);

        var tables = metadata.Tables.Select(t => new TableSummary
        {
            SchemaName = t.SchemaName,
            TableName = t.TableName,
            ColumnCount = t.Columns.Count,
            HasPrimaryKey = t.Columns.Any(c => c.IsPrimaryKey),
            ForeignKeyCount = t.Columns.Count(c => c.IsForeignKey),
            IndexCount = t.Indexes.Count,
            Description = t.Description
        }).ToList();

        return new TableListResult
        {
            Tables = tables,
            TotalCount = tables.Count,
            RefreshedAt = metadata.RefreshedAt
        };
    }

    [Description("Get detailed metadata for a specific table including all columns, data types, constraints, and indexes.")]
    public async Task<TableMetadataResult> GetTableMetadata(
        [Description("The data source ID")] int dataSourceId,
        [Description("The table name to get metadata for")] string tableName,
        [Description("Optional schema name (defaults to public/dbo)")] string? schemaName = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("GetTableMetadata called for {TableName} in DataSource {DataSourceId}", tableName, dataSourceId);

        var metadata = await _metadataService.GetMetadataAsync(dataSourceId, cancellationToken);

        var table = metadata.Tables.FirstOrDefault(t =>
            t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
            (schemaName == null || t.SchemaName.Equals(schemaName, StringComparison.OrdinalIgnoreCase)));

        if (table == null)
        {
            return new TableMetadataResult
            {
                Success = false,
                ErrorMessage = $"Table '{tableName}' not found in data source"
            };
        }

        return new TableMetadataResult
        {
            Success = true,
            SchemaName = table.SchemaName,
            TableName = table.TableName,
            Description = table.Description,
            Columns = table.Columns.Select(c => new ColumnInfo
            {
                Name = c.ColumnName,
                DataType = c.DataType,
                IsNullable = c.IsNullable,
                IsPrimaryKey = c.IsPrimaryKey,
                IsForeignKey = c.IsForeignKey,
                ForeignKeyTable = c.ForeignKeyTable,
                ForeignKeyColumn = c.ForeignKeyColumn,
                DefaultValue = c.DefaultValue,
                MaxLength = c.MaxLength,
                Description = c.Description,
                OrdinalPosition = c.OrdinalPosition
            }).OrderBy(c => c.OrdinalPosition).ToList(),
            Indexes = table.Indexes.Select(i => new IndexInfo
            {
                Name = i.IndexName,
                IsUnique = i.IsUnique,
                IsPrimaryKey = i.IsPrimaryKey,
                Columns = i.Columns.ToList()
            }).ToList()
        };
    }

    [Description("Get foreign key relationships for a table - both outgoing references and tables that reference this table.")]
    public async Task<RelationshipsResult> GetRelationships(
        [Description("The data source ID")] int dataSourceId,
        [Description("The table name to get relationships for")] string tableName,
        [Description("Optional schema name")] string? schemaName = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("GetRelationships called for {TableName} in DataSource {DataSourceId}", tableName, dataSourceId);

        var metadata = await _metadataService.GetMetadataAsync(dataSourceId, cancellationToken);

        var table = metadata.Tables.FirstOrDefault(t =>
            t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
            (schemaName == null || t.SchemaName.Equals(schemaName, StringComparison.OrdinalIgnoreCase)));

        if (table == null)
        {
            return new RelationshipsResult
            {
                Success = false,
                ErrorMessage = $"Table '{tableName}' not found"
            };
        }

        // Outgoing relationships (this table references other tables)
        var outgoingRefs = table.Columns
            .Where(c => c.IsForeignKey && !string.IsNullOrEmpty(c.ForeignKeyTable))
            .Select(c => new ForeignKeyReference
            {
                SourceColumn = c.ColumnName,
                ReferencedTable = c.ForeignKeyTable!,
                ReferencedColumn = c.ForeignKeyColumn
            }).ToList();

        // Incoming relationships (other tables reference this table)
        var incomingRefs = metadata.Tables
            .SelectMany(t => t.Columns
                .Where(c => c.IsForeignKey &&
                       c.ForeignKeyTable?.Equals(tableName, StringComparison.OrdinalIgnoreCase) == true)
                .Select(c => new ForeignKeyReference
                {
                    SourceTable = t.TableName,
                    SourceColumn = c.ColumnName,
                    ReferencedColumn = c.ForeignKeyColumn
                }))
            .ToList();

        return new RelationshipsResult
        {
            Success = true,
            TableName = tableName,
            OutgoingReferences = outgoingRefs,
            IncomingReferences = incomingRefs
        };
    }

    [Description("Query sample data from a table to understand typical values and data patterns.")]
    public async Task<SampleDataResult> QuerySampleData(
        [Description("The data source ID")] int dataSourceId,
        [Description("The table name to query")] string tableName,
        [Description("Number of sample rows to fetch (max 1000)")] int rowCount = 5,
        [Description("Optional schema name")] string? schemaName = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("QuerySampleData called for {TableName} in DataSource {DataSourceId}", tableName, dataSourceId);

        rowCount = Math.Min(rowCount, 1000); // Enforce maximum

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var dataSource = await context.DataSources
            .FirstOrDefaultAsync(ds => ds.Id == dataSourceId, cancellationToken);

        if (dataSource == null)
        {
            return new SampleDataResult
            {
                Success = false,
                ErrorMessage = "Data source not found"
            };
        }

        try
        {
            var fullTableName = string.IsNullOrEmpty(schemaName)
                ? QuoteIdentifier(tableName, dataSource.DatabaseEngineType)
                : $"{QuoteIdentifier(schemaName, dataSource.DatabaseEngineType)}.{QuoteIdentifier(tableName, dataSource.DatabaseEngineType)}";

            var limitClause = dataSource.DatabaseEngineType == DatabaseEngineType.MSSQL
                ? $"TOP {rowCount}"
                : "";
            var offsetClause = dataSource.DatabaseEngineType != DatabaseEngineType.MSSQL
                ? $"LIMIT {rowCount}"
                : "";

            var query = dataSource.DatabaseEngineType == DatabaseEngineType.MSSQL
                ? $"SELECT {limitClause} * FROM {fullTableName}"
                : $"SELECT * FROM {fullTableName} {offsetClause}";

            var decryptedConnectionString = _encryptionService.Decrypt(dataSource.ConnectionString);
            await using var connection = DbConnectionFactory.CreateConnection(dataSource.DatabaseEngineType, decryptedConnectionString);
            await connection.OpenAsync(cancellationToken);

            var rows = (await connection.QueryAsync<dynamic>(query)).ToList();

            // Convert to list of dictionaries for JSON serialization
            var sampleRows = rows.Select(row =>
            {
                var dict = (IDictionary<string, object>)row;
                return dict.ToDictionary(
                    kv => kv.Key,
                    kv => FormatSampleValue(kv.Value));
            }).ToList();

            return new SampleDataResult
            {
                Success = true,
                TableName = tableName,
                RowCount = sampleRows.Count,
                SampleRows = sampleRows,
                ColumnNames = sampleRows.FirstOrDefault()?.Keys.ToList() ?? []
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query sample data for {TableName}", tableName);
            return new SampleDataResult
            {
                Success = false,
                ErrorMessage = $"Failed to query sample data: {ex.Message}"
            };
        }
    }

    [Description("Save a documentation section to the database. Use this to persist generated documentation.")]
    public async Task<SaveSectionResult> SaveDocumentationSection(
        [Description("The documentation ID to add the section to")] int documentationId,
        [Description("Section title")] string title,
        [Description("Section type: Overview, Architecture, TableDetail, DomainGroup, Relationships, or DataQuality")] string sectionType,
        [Description("The markdown content for this section")] string content,
        [Description("Table name if this is a table-specific section")] string? tableName = null,
        [Description("Sort order for display (lower numbers appear first)")] int sortOrder = 0,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("SaveDocumentationSection called for Documentation {DocumentationId}, Section: {Title}", documentationId, title);

        if (!Enum.TryParse<SectionType>(sectionType, true, out var sectionTypeEnum))
        {
            return new SaveSectionResult
            {
                Success = false,
                ErrorMessage = $"Invalid section type: {sectionType}. Valid types are: {string.Join(", ", Enum.GetNames<SectionType>())}"
            };
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var documentation = await context.DataSourceDocumentations
            .FirstOrDefaultAsync(d => d.Id == documentationId, cancellationToken);

        if (documentation == null)
        {
            return new SaveSectionResult
            {
                Success = false,
                ErrorMessage = "Documentation not found"
            };
        }

        var now = DateTime.UtcNow;
        var section = new DocumentationSection
        {
            DocumentationId = documentationId,
            Title = title,
            SectionType = sectionTypeEnum,
            TableName = tableName,
            SortOrder = sortOrder,
            AiGeneratedContent = content,
            IsUserEdited = false,
            ContentFormat = ContentFormat.Markdown,
            CreatedBy = "documentation-agent",
            ModifiedBy = "documentation-agent"
        };

        context.DocumentationSections.Add(section);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            var fullError = GetFullExceptionMessage(ex);
            _logger.LogError(ex, "Failed to save documentation section for table {TableName}: {Error}", tableName, fullError);

            return new SaveSectionResult
            {
                Success = false,
                ErrorMessage = fullError
            };
        }

        return new SaveSectionResult
        {
            Success = true,
            SectionId = section.Id
        };
    }

    [Description("Get all existing documentation sections for a documentation, useful for context when generating synthesis/overview sections.")]
    public async Task<GetSectionsResult> GetExistingSections(
        [Description("The documentation ID")] int documentationId,
        [Description("Optional: filter by section type")] string? sectionType = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("GetExistingSections called for Documentation {DocumentationId}", documentationId);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.DocumentationSections
            .Where(s => s.DocumentationId == documentationId);

        if (!string.IsNullOrEmpty(sectionType) && Enum.TryParse<SectionType>(sectionType, true, out var typeFilter))
        {
            query = query.Where(s => s.SectionType == typeFilter);
        }

        var sections = await query
            .OrderBy(s => s.SortOrder)
            .Select(s => new SectionSummary
            {
                Id = s.Id,
                Title = s.Title,
                SectionType = s.SectionType.ToString(),
                TableName = s.TableName,
                SortOrder = s.SortOrder,
                ContentLength = s.AiGeneratedContent.Length,
                IsUserEdited = s.IsUserEdited
            })
            .ToListAsync(cancellationToken);

        return new GetSectionsResult
        {
            Success = true,
            Sections = sections,
            TotalCount = sections.Count
        };
    }

    [Description("Update the progress of the documentation agent run. Call this to report progress to the user.")]
    public async Task<UpdateProgressResult> UpdateProgress(
        [Description("The agent run ID")] int agentRunId,
        [Description("Current phase: Discovery, TableDocumentation, or Synthesis")] string phase,
        [Description("Progress percentage (0-100)")] int progressPercent,
        [Description("Human-readable status message")] string message,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("UpdateProgress called for AgentRun {AgentRunId}: {Phase} - {Percent}%", agentRunId, phase, progressPercent);

        if (!Enum.TryParse<DocumentationAgentPhase>(phase, true, out var phaseEnum))
        {
            return new UpdateProgressResult
            {
                Success = false,
                ErrorMessage = $"Invalid phase: {phase}"
            };
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var agentRun = await context.DocumentationAgentRuns
            .FirstOrDefaultAsync(r => r.Id == agentRunId, cancellationToken);

        if (agentRun == null)
        {
            return new UpdateProgressResult
            {
                Success = false,
                ErrorMessage = "Agent run not found"
            };
        }

        agentRun.CurrentPhase = phaseEnum;
        agentRun.ProgressPercent = Math.Clamp(progressPercent, 0, 100);
        agentRun.ProgressMessage = message;
        agentRun.LastCheckpointAt = DateTime.UtcNow;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            var fullError = GetFullExceptionMessage(ex);
            _logger.LogError(ex, "Failed to update progress for AgentRun {AgentRunId}: {Error}", agentRunId, fullError);

            return new UpdateProgressResult
            {
                Success = false,
                ErrorMessage = fullError
            };
        }

        return new UpdateProgressResult
        {
            Success = true
        };
    }

    private static string QuoteIdentifier(string identifier, DatabaseEngineType dbType)
    {
        return dbType switch
        {
            DatabaseEngineType.MSSQL => $"[{identifier}]",
            DatabaseEngineType.PostgreSQL => $"\"{identifier}\"",
            DatabaseEngineType.MySQL => $"`{identifier}`",
            _ => identifier
        };
    }

    private static string? FormatSampleValue(object? value)
    {
        if (value == null || value == DBNull.Value)
            return null;

        return value switch
        {
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss"),
            byte[] bytes => $"[{bytes.Length} bytes]",
            _ when value.ToString()?.Length > 100 => value.ToString()?[..100] + "...",
            _ => value.ToString()
        };
    }

    /// <summary>
    /// Extracts the full exception message including all inner exceptions.
    /// Useful for EF Core exceptions where the real error is in the inner exception.
    /// </summary>
    private static string GetFullExceptionMessage(Exception? exception)
    {
        if (exception == null)
            return "Unknown error";

        var messages = new List<string>();
        var currentException = exception;

        while (currentException != null)
        {
            messages.Add($"{currentException.GetType().Name}: {currentException.Message}");
            currentException = currentException.InnerException;
        }

        return string.Join(" --> ", messages);
    }
}
