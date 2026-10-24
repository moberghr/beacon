using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;
using PostgreSqlConnector = Beacon.Connector.PostgreSql.ServiceCollectionExtensions;

namespace Beacon.Tests.Unit.DataSourceTransport;

/// <summary>
/// A PostgreSQL connection that carries its own credentials opens without the Beacon host's Kerberos or Windows identity:
/// the connector's connection string refuses a server's GSS or SSPI request and asks for no GSS encryption, unless the
/// connection says otherwise; one without credentials of its own is left as it is. The last test answers the sign-in
/// from a local socket that speaks only the first message of the protocol, so no database is needed.
/// </summary>
[TestFixture]
public class PostgreSqlHostIdentityTests
{
    [TestCase("Host=db.internal;Username=reader;Password=pw;SSL Mode=VerifyFull")]
    [TestCase("Host=db.internal;Username=reader;SSL Certificate=/etc/beacon/client.pem;SSL Mode=VerifyFull")]
    [TestCase("Host=db.internal;Username=reader;Password=pw;GSS Encryption Mode=Prefer")]
    public void AConnectionWithItsOwnCredentials_RefusesHostIdentitySignIn(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(PostgreSqlConnector.WithConnectionDefaults(connectionString));

        builder.RequireAuth.Should().Be(PostgreSqlConnector.NoHostIdentityAuth);
        builder.GssEncryptionMode.Should().Be(GssEncryptionMode.Disable);
    }

    [Test]
    public void AnExplicitChoice_IsKept()
    {
        var connectionString = "Host=db.internal;Username=reader;Password=pw;Require Auth=GSS;GSS Encryption Mode=Require";

        var builder = new NpgsqlConnectionStringBuilder(PostgreSqlConnector.WithConnectionDefaults(connectionString));

        builder.RequireAuth.Should().Be("GSS");
        builder.GssEncryptionMode.Should().Be(GssEncryptionMode.Require);
    }

    [Test]
    public void AConnectionWithoutCredentialsOfItsOwn_IsLeftAsItIs()
    {
        var connectionString = "Host=db.internal;Username=reader;Timeout=10";

        PostgreSqlConnector.WithConnectionDefaults(connectionString).Should().Be(connectionString);
    }

    [Test]
    public async Task AServerAskingForKerberos_IsRefused_BeforeAnyTicketIsSent()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = AnswerWithKerberosRequest(listener);
        var connectionString = $"Host=127.0.0.1;Port={port};Username=reader;Password=pw;SSL Mode=Disable;Timeout=5;Pooling=false";

        await using var connection = new NpgsqlConnection(PostgreSqlConnector.WithConnectionDefaults(connectionString));
        var act = () => connection.OpenAsync();

        (await act.Should().ThrowAsync<NpgsqlException>()).Which.Message.Should().Contain("\"GSS\" authentication method is not allowed");
        await server;
    }

    // Reads the startup message and answers AuthenticationGSS ('R', length 8, code 7).
    private static async Task AnswerWithKerberosRequest(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var length = new byte[4];
        await stream.ReadExactlyAsync(length);
        var startup = new byte[BinaryPrimitives.ReadInt32BigEndian(length) - 4];
        await stream.ReadExactlyAsync(startup);

        var request = new byte[9];
        request[0] = (byte)'R';
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(1), 8);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(5), 7);
        await stream.WriteAsync(request);
        await stream.FlushAsync();

        // Wait for the client to close its end (a reset is a close too).
        var rest = new byte[64];
        try
        {
            while (await stream.ReadAsync(rest) > 0)
            {
                // Nothing else is answered.
            }
        }
        catch (IOException)
        {
            // The client reset the connection.
        }
    }
}
