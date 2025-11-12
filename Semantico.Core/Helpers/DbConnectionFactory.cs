using System.Data.Common;
using System.Data.SqlClient;
using MySql.Data.MySqlClient;
using Npgsql;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models;

namespace Semantico.Core.Helpers;

public static class DbConnectionFactory
{
    public static DbConnection CreateConnection(DatabaseEngineType dbEngineType, string connectionString) =>
        dbEngineType switch
        {
            DatabaseEngineType.PostgreSQL => new NpgsqlConnection(connectionString),
            DatabaseEngineType.MSSQL => new SqlConnection(connectionString),
            DatabaseEngineType.MySQL => new MySqlConnection(connectionString),
            _ => throw new SemanticoException($"Unsupported database engine: {dbEngineType}")
        };
}
