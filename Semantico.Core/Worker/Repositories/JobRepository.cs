using Dapper;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using System.Data.Common;

namespace Semantico.Core.Worker.Repositories;

internal interface IJobRepository
{
    Task<List<object>> ExecuteQueryAsync(DatabaseEngineType dbEngineType, string connectionString, string sqlQuery);
}

internal class JobRepository : IJobRepository
{
    public async Task<List<object>> ExecuteQueryAsync(DatabaseEngineType dbEngineType, string connectionString, string sqlQuery)
    {
        using var connection = DbConnectionFactory.CreateConnection(dbEngineType, connectionString);
        await connection.OpenAsync();

        // Replace newline, carriage return, and tab characters with a space
        var cleanedSql = sqlQuery.Replace("\n", " ")
                            .Replace("\r", " ")
                            .Replace("\t", " ")
                            .Trim();

        var results = await connection.QueryAsync<object>(cleanedSql);

        return results.ToList();
    }
}
