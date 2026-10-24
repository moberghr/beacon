using System.Diagnostics;
using System.Text.Json;
using Beacon.Core.Data.Entities;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// The configuration reading, connection-test and validation-verdict code the BigQuery, Databricks, CloudWatch and API
/// connectors share. It reaches no data source and holds no read-only or error-masking code: each connector decrypts its
/// own configuration through the connection resolver and keeps its own execute overrides (§1.5).
/// </summary>
internal static class ConnectorProviderSupport
{
    /// <summary>
    /// Reads a connector's decrypted configuration JSON. A document that reads as null fails with
    /// <paramref name="unreadableMessage"/>; when <paramref name="invalidJsonPrefix"/> is given, malformed JSON fails with
    /// that prefix followed by the parser's message, otherwise the parser's exception propagates.
    /// </summary>
    public static TConfig ParseConfiguration<TConfig>(
        string json,
        string unreadableMessage,
        string? invalidJsonPrefix = null,
        JsonSerializerOptions? options = null)
        where TConfig : class
    {
        try
        {
            return JsonSerializer.Deserialize<TConfig>(json, options)
                ?? throw new BeaconException(unreadableMessage);
        }
        catch (JsonException ex) when (invalidJsonPrefix != null)
        {
            throw new BeaconException($"{invalidJsonPrefix}{ex.Message}", ex);
        }
    }

    /// <summary>
    /// Times <paramref name="probe"/> and shapes the outcome: its returned details on success, the described failure
    /// chain on error (the exception is logged, as the connection tests always did).
    /// </summary>
    public static async Task<ConnectionTestResult> TestConnectionAsync(
        ILogger logger,
        DataSource dataSource,
        string engine,
        Func<Task<Dictionary<string, object?>>> probe)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var connectionInfo = await probe();

            stopwatch.Stop();
            return new ConnectionTestResult
            {
                Success = true,
                TestDurationMs = stopwatch.Elapsed.TotalMilliseconds,
                ConnectionInfo = connectionInfo
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Connection test failed for {Engine} data source {DataSourceId}", engine, dataSource.Id);
            stopwatch.Stop();
            return new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = ConnectionFailureDescriber.Describe(ex),
                TestDurationMs = stopwatch.Elapsed.TotalMilliseconds
            };
        }
    }

    /// <summary>A verdict that is valid exactly when there are no errors.</summary>
    public static Task<QueryValidationResult> ValidationVerdict(
        List<string> errors,
        List<string>? warnings = null,
        List<string>? suggestions = null)
    {
        return Task.FromResult(new QueryValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings ?? [],
            Suggestions = suggestions ?? []
        });
    }
}
