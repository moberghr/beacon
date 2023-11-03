using Dapper;
using Npgsql;
using MySql.Data.MySqlClient;
using Semantico.Api.Adapters;
using Semantico.Api.Data.Enums;
using Semantico.Api.Types;
using System.Data.Common;
using System.Data.SqlClient;
using System.Text.Json;
using Semantico.Api.Handlers.Projects;

namespace Semantico.Api.Worker.Repositories;

public interface IJobRepository
{
    Task<QueryResult> GetQueryResultsAsync(GetProjectsResponseListData project, string sqlQuery);
}

public class JobRepository : IJobRepository
{
    public async Task<QueryResult> GetQueryResultsAsync(GetProjectsResponseListData project, string sqlQuery)
    {
        using var connection = GetDbConnection(project.DatabaseEngineType, project.ConnectionString);
        await connection.OpenAsync();

        var results = await connection.QueryAsync<object>(sqlQuery);

        var recordCounter = results.Count();
        var queryResults = results.Take(10).ToList();

        return new QueryResult
        {
            QueryResults = JsonSerializer.Serialize(queryResults),
            TotalRecords = recordCounter,
            ProjectName = project.Name,
            SqlQuery = sqlQuery,
        };
    }

    private static DbConnection GetDbConnection(DatabaseEngineType dbEngineType, string connectionString) => dbEngineType switch
    {
        DatabaseEngineType.PostgreSQL => new NpgsqlConnection(connectionString),
        DatabaseEngineType.MSSQL => new SqlConnection(connectionString),
        DatabaseEngineType.MySQL => new MySqlConnection(connectionString),
        _ => throw new SemanticoException($"Unsupported database engine.")
    };
}
