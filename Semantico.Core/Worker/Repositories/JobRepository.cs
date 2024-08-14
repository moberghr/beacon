using Dapper;
using Npgsql;
using MySql.Data.MySqlClient;
using Semantico.Core.Data.Enums;
using System.Data.Common;
using System.Data.SqlClient;
using Semantico.Core.Models;

namespace Semantico.Core.Worker.Repositories;

internal interface IJobRepository
{
    Task<List<object>> ExecuteQueryAsync(DatabaseEngineType dbEngineType, string connectionString, string sqlQuery);
}

internal class JobRepository : IJobRepository
{
    public async Task<List<object>> ExecuteQueryAsync(DatabaseEngineType dbEngineType, string connectionString, string sqlQuery)
    {
        using var connection = GetDbConnection(dbEngineType, connectionString);
        await connection.OpenAsync();

        var results = await connection.QueryAsync<object>(sqlQuery);

        return results.ToList();
    }

    private static DbConnection GetDbConnection(DatabaseEngineType dbEngineType, string connectionString) => dbEngineType switch
    {
        DatabaseEngineType.PostgreSQL => new NpgsqlConnection(connectionString),
        DatabaseEngineType.MSSQL => new SqlConnection(connectionString),
        DatabaseEngineType.MySQL => new MySqlConnection(connectionString),
        _ => throw new SemanticoException($"Unsupported database engine.")
    };
}
