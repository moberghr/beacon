using System.Data.Common;
using Npgsql;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.DataMigration;
using Beacon.Core.Helpers;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beacon.Connector.PostgreSql;

public static class ServiceCollectionExtensions
{
    // Every authentication method but the host's Kerberos (GSS) and Windows (SSPI) identity.
    internal const string NoHostIdentityAuth = "!GSS,!SSPI";

    public static BeaconBuilder AddPostgreSqlConnector(this BeaconBuilder builder)
    {
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, cs => new NpgsqlConnection(WithConnectionDefaults(cs)), x => NpgsqlConnection.ClearPool((NpgsqlConnection)x));
        ConnectorRegistry.RegisterDatabaseEngine(DatabaseEngineType.PostgreSQL, "PostgreSQL");
        builder.Services.AddTransient<IDatabaseMetadataExtractor, PostgreSqlMetadataExtractor>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataSourceTransportInspector, PostgreSqlTransportInspector>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataSourceConnectionOptionInspector, PostgreSqlConnectionOptionInspector>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IMigrationBulkWriter, PostgreSqlMigrationBulkWriter>());
        return builder;
    }

    // The connection string every PostgreSQL connection opens with: the connect timeout (Timeout) bounded as
    // DbConnectionFactory.ConnectTimeoutSeconds sets it and, for a connection that carries its own credentials, no
    // Kerberos or SSPI sign-in as the Beacon host (WithoutHostIdentity). A string that needs neither change is used as it is.
    internal static string WithConnectionDefaults(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var timeout = DbConnectionFactory.ConnectTimeoutSeconds(builder.Timeout);
        var changed = timeout != builder.Timeout;
        if (changed)
        {
            builder.Timeout = timeout;
        }

        if (WithoutHostIdentity(builder))
        {
            changed = true;
        }

        return changed ? builder.ConnectionString : connectionString;
    }

    // Npgsql answers a server that asks for GSS or SSPI with the Beacon host's Kerberos or Windows identity, whatever the
    // password, and by default (GSS Encryption Mode=Prefer) asks the server for GSS encryption with that identity. A
    // connection that carries its own credentials (a Password or an SSL Certificate) never needs either: unless it says
    // otherwise, it refuses those sign-ins (Require Auth=!GSS,!SSPI) and does not ask for GSS encryption. An explicit
    // Require Auth that admits GSS or SSPI, or GSS Encryption Mode=Require, is the option inspector's to refuse (lifted by
    // Beacon:DataSources:AllowHostCredentials); a connection without credentials of its own (only accepted under it, or a
    // host-managed one) is left as it is.
    private static bool WithoutHostIdentity(NpgsqlConnectionStringBuilder builder)
    {
        if (string.IsNullOrEmpty(builder.Password) && string.IsNullOrEmpty(builder.SslCertificate))
        {
            return false;
        }

        var changed = false;
        if (string.IsNullOrWhiteSpace(builder.RequireAuth))
        {
            builder.RequireAuth = NoHostIdentityAuth;
            changed = true;
        }

        if (builder.GssEncryptionMode == GssEncryptionMode.Prefer)
        {
            builder.GssEncryptionMode = GssEncryptionMode.Disable;
            changed = true;
        }

        return changed;
    }
}
