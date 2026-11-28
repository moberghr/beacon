using System.Text;
using System.Text.Json;
using Beacon.AI.Services;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.AI.Handlers.DataSources;

/// <summary>
/// Proposes descriptions for the columns of one table that have none (null or blank) and no proposed column-description
/// patch in the project yet — only the project's own patches count, as for the learning job's proposals. At most
/// <see cref="DescribeTableColumnsCommand.MaxColumns"/> columns, in ordinal order, go to the LLM in one completion through
/// the provider (and so the request queue); with none, nothing is sent. Each description the answer carries for a column
/// it was asked about becomes a proposed patch quoting the column's description as it is now, with the model that wrote
/// it, so applying it records a generated description; the catalog itself is not written. Stored sample values go along
/// only as the ask path shows them: screened by <see cref="StoredSampleScreen"/> under the settings of every live project
/// that links the source (none while sample-value collection is off, which <c>Beacon:Mcp:ForbidRowDataToLlm</c> pins), and
/// the request is then logged by the row-data egress log. A table whose schema or table name holds a dot is refused: the
/// patch's target (<c>schema.table.column</c>) could not name it.
/// </summary>
internal sealed class DescribeTableColumnsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ILlmProvider llmProvider,
    ILlmRowDataEgress rowDataEgress,
    ISharedSourceSettings sharedSourceSettings,
    IQueryGuardrailService guardrail,
    ISecurityAuditRecorder securityAudit,
    ILogger<DescribeTableColumnsHandler> logger)
    : IRequestHandler<DescribeTableColumnsCommand, DescribeTableColumnsResult>
{
    /// <summary>What a caller is told when the answer could not be read; never the provider's text.</summary>
    internal const string UnreadableAnswerMessage =
        "The LLM's answer could not be read as column descriptions. Try again.";

    internal const string NotLinkedMessage = "The data source is not linked to this project.";

    internal const string TableNotFoundMessage = "The table was not found in the data source's stored metadata.";

    internal const string DottedNameMessage =
        "Descriptions cannot be proposed for a table whose schema or table name contains a dot.";

    /// <summary>The longest description stored from an answer.</summary>
    internal const int MaxDescriptionLength = 500;

    /// <summary>The stored sample values shown per column, each cut to <see cref="MaxSampleValueLength"/> characters.</summary>
    internal const int MaxSampleValues = 5;

    internal const int MaxSampleValueLength = 100;

    private const int MaxTargetIdentifierLength = 500;
    private const int MaxTableDescriptionLength = 500;
    private const int MaxModelLength = 200;
    private const string UnknownModel = "unknown";

    private const string Reasoning =
        "Proposed by the column description generator for a column without a description. Review it before applying.";

    private const string SystemPrompt =
        "You write documentation for database columns. For each column listed in the table columns block, write one plain "
        + "sentence of at most 300 characters that says what the column holds, judging by its name, its data type, its table "
        + "and any example values. Answer with exactly one JSON object and nothing else: each key is a column name exactly "
        + "as listed, each value is that column's description. Leave out a column you cannot describe; never add a column "
        + "that is not listed.\n\n"
        + PromptData.Rule;

    public async Task<DescribeTableColumnsResult> Handle(DescribeTableColumnsCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SchemaName) || string.IsNullOrWhiteSpace(request.TableName))
        {
            throw new InvalidOperationException("A schema name and a table name are required.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var linked = await context.ProjectDataSources
            .WhereReadable()
            .Where(x => x.ProjectId == request.ProjectId)
            .Where(x => x.DataSourceId == request.DataSourceId)
            .AnyAsync(cancellationToken);
        if (!linked)
        {
            throw new BeaconNotFoundException(NotLinkedMessage);
        }

        var table = await context.DatabaseMetadata
            .Where(x => x.DataSourceId == request.DataSourceId)
            .Where(x => x.SchemaName == request.SchemaName)
            .Where(x => x.TableName == request.TableName)
            .Select(x =>
                new
                {
                    x.Id,
                    x.SchemaName,
                    x.TableName,
                    x.TableDescription
                })
            .FirstOrDefaultAsync(cancellationToken);
        if (table == null)
        {
            throw new InvalidOperationException(TableNotFoundMessage);
        }

        if (table.SchemaName.Contains('.') || table.TableName.Contains('.'))
        {
            throw new InvalidOperationException(DottedNameMessage);
        }

        var prefix = $"{table.SchemaName}.{table.TableName}.";
        var maxColumnNameLength = MaxTargetIdentifierLength - prefix.Length;
        var columns = await context.ColumnMetadata
            .Where(x => x.DatabaseMetadataId == table.Id)
            .Where(x => string.IsNullOrWhiteSpace(x.Description))
            .Where(x => x.ColumnName.Length <= maxColumnNameLength)
            .Where(x => !context.McpDocumentationPatches
                .Where(y => y.ProjectId == request.ProjectId)
                .Where(y => y.DataSourceId == request.DataSourceId)
                .Where(y => y.TargetType == McpDocPatchTarget.ColumnDescription)
                .Where(y => y.Status == McpDocPatchStatus.Proposed)
                .Any(y => y.TargetIdentifier.ToLower() == (prefix + x.ColumnName).ToLower()))
            .OrderBy(x => x.OrdinalPosition)
            .ThenBy(x => x.Id)
            .Take(DescribeTableColumnsCommand.MaxColumns)
            .Select(x =>
                new ColumnToDescribe(
                    x.ColumnName,
                    x.DataType,
                    x.IsPrimaryKey,
                    x.Description,
                    x.SampleValues))
            .ToListAsync(cancellationToken);

        if (columns.Count == 0)
        {
            return new DescribeTableColumnsResult(0, 0);
        }

        var settings = await sharedSourceSettings.GetEffectiveSettingsAsync(request.ProjectId, [request.DataSourceId], cancellationToken);
        var shown = columns
            .Select(x =>
                new ShownColumn(
                    x,
                    PromptData.OneLine(x.ColumnName),
                    ShownSamples(x, settings)))
            .ToList();
        var samplesSent = shown.Any(x => x.Samples.Count > 0);

        var llmRequest = new LlmRequest
        {
            SystemPrompt = SystemPrompt,
            Messages = [new ChatMessage(ConversationRole.User, BuildUserPrompt(table.SchemaName, table.TableName, table.TableDescription, shown))],
            Temperature = 0.2m,
            MaxTokens = 8192,
            Operation = LlmOperations.DocumentationDescribeColumns
        };
        var provider = rowDataEgress.Track(
            llmProvider,
            new LlmRowDataScope(request.ProjectId, [request.DataSourceId], samplesSent ? [LlmRowDataCategories.SampleValues] : []));
        var response = await provider.CompleteAsync(llmRequest, cancellationToken);

        var descriptions = ReadDescriptions(response.Content, shown, request);
        var model = ModelOf(response.Model);
        foreach (var (column, description) in descriptions)
        {
            context.McpDocumentationPatches.Add(new McpDocumentationPatch
            {
                ProjectId = request.ProjectId,
                DataSourceId = request.DataSourceId,
                TargetType = McpDocPatchTarget.ColumnDescription,
                TargetIdentifier = prefix + column.ColumnName,
                CurrentContent = column.Description,
                ProposedContent = description,
                Reasoning = Reasoning,
                SupportingSignalCount = 0,
                Status = McpDocPatchStatus.Proposed,
                GeneratedByModel = model
            });
        }

        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.ColumnDescriptionsProposed)
                .On(SecurityAuditResources.DataSource, request.DataSourceId)
                .InProject(request.ProjectId)
                .With(new SecurityAuditDetails()
                    .Add("tableId", table.Id)
                    .Add("columnsAsked", columns.Count)
                    .Add("proposalsCreated", descriptions.Count)),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Proposed {ProposalCount} column descriptions for table {TableId} of data source {DataSourceId} in project {ProjectId} ({ColumnCount} columns asked)",
            descriptions.Count, table.Id, request.DataSourceId, request.ProjectId, columns.Count);

        return new DescribeTableColumnsResult(columns.Count, descriptions.Count);
    }

    // The stored samples of a column that may be shown now (as the ask path screens them), the first few, each cut short.
    private IReadOnlyList<string> ShownSamples(ColumnToDescribe column, McpSettingsData settings)
    {
        var stored = SchemaContextFormatter.DeserializeSampleValues(column.SampleValues);
        var screened = StoredSampleScreen.Screen(guardrail, column.ColumnName, stored, settings);
        if (screened == null)
        {
            return [];
        }

        return screened
            .Take(MaxSampleValues)
            .Select(x => x.Length > MaxSampleValueLength ? x[..MaxSampleValueLength] : x)
            .ToList();
    }

    private static string BuildUserPrompt(string schemaName, string tableName, string? tableDescription, IReadOnlyList<ShownColumn> columns)
    {
        var block = new StringBuilder();
        block.AppendLine($"Table: {PromptData.OneLine(schemaName)}.{PromptData.OneLine(tableName)}");
        if (!string.IsNullOrWhiteSpace(tableDescription))
        {
            var description = tableDescription.Length > MaxTableDescriptionLength
                ? tableDescription[..MaxTableDescriptionLength]
                : tableDescription;
            block.AppendLine($"Table description: {PromptData.OneLine(description)}");
        }

        block.AppendLine("Columns:");
        foreach (var column in columns)
        {
            block.Append($"- {column.ShownName} ({PromptData.OneLine(column.Column.DataType)}");
            if (column.Column.IsPrimaryKey)
            {
                block.Append(", primary key");
            }

            block.Append(')');
            if (column.Samples.Count > 0)
            {
                block.Append($" Examples: [{string.Join(", ", column.Samples.Select(PromptData.OneLine))}]");
            }

            block.AppendLine();
        }

        return "Describe each column listed in the data block below.\n\n" + PromptData.Fence("table columns", block.ToString());
    }

    // The descriptions the answer gives for the columns it was asked about: a key matches a column's name as the prompt
    // showed it, ignoring case (a key two columns share names neither); values are trimmed, blank ones dropped, long ones
    // cut. An answer without a readable JSON object, or one that describes none of the columns, is refused in Beacon's
    // words, never the provider's.
    private List<(ColumnToDescribe Column, string Description)> ReadDescriptions(
        string? content,
        IReadOnlyList<ShownColumn> columns,
        DescribeTableColumnsCommand request)
    {
        var json = ExtractJsonObject(content ?? "");
        if (json == null)
        {
            logger.LogWarning(
                "Column description answer for data source {DataSourceId} in project {ProjectId} held no JSON object",
                request.DataSourceId, request.ProjectId);
            throw new InvalidOperationException(UnreadableAnswerMessage);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                "Column description answer for data source {DataSourceId} in project {ProjectId} could not be read ({Failure})",
                request.DataSourceId, request.ProjectId, FailureCodes.Describe(ex));
            throw new InvalidOperationException(UnreadableAnswerMessage);
        }

        using (document)
        {
            var byShownName = columns
                .GroupBy(x => x.ShownName, StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Count() == 1)
                .ToDictionary(x => x.Key, x => x.Single().Column, StringComparer.OrdinalIgnoreCase);
            var descriptions = new Dictionary<ColumnToDescribe, string>();
            var keys = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                keys++;
                if (property.Value.ValueKind != JsonValueKind.String
                    || !byShownName.TryGetValue(property.Name.Trim(), out var column)
                    || descriptions.ContainsKey(column))
                {
                    continue;
                }

                var description = property.Value.GetString()?.Trim();
                if (string.IsNullOrEmpty(description))
                {
                    continue;
                }

                descriptions[column] = description.Length > MaxDescriptionLength
                    ? description[..MaxDescriptionLength].TrimEnd()
                    : description;
            }

            // An answer that describes none of the columns asked (an empty or nested object, other keys) is as useless as
            // an unreadable one: refused, nothing saved, no event.
            if (descriptions.Count == 0)
            {
                logger.LogWarning(
                    "Column description answer for data source {DataSourceId} in project {ProjectId} described none of the {ColumnCount} columns asked ({KeyCount} keys in the answer)",
                    request.DataSourceId, request.ProjectId, columns.Count, keys);
                throw new InvalidOperationException(UnreadableAnswerMessage);
            }

            return columns
                .Where(x => descriptions.ContainsKey(x.Column))
                .Select(x => (x.Column, descriptions[x.Column]))
                .ToList();
        }
    }

    private static string ModelOf(string? model)
    {
        var trimmed = model?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return UnknownModel;
        }

        return trimmed.Length > MaxModelLength ? trimmed[..MaxModelLength] : trimmed;
    }

    // The first balanced JSON object in the text (an answer may wrap it in prose or a code fence).
    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var character = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = inString;
                continue;
            }

            if (character == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (character == '{')
            {
                depth++;
            }
            else if (character == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        return null;
    }

    private sealed record ColumnToDescribe(string ColumnName, string DataType, bool IsPrimaryKey, string? Description, string? SampleValues);

    private sealed record ShownColumn(ColumnToDescribe Column, string ShownName, IReadOnlyList<string> Samples);
}
