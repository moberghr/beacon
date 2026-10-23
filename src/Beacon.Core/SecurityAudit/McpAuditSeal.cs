using Beacon.Core.Data.Entities;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// The seal of an MCP audit row (<see cref="McpAuditLog"/>): a keyed HMAC-SHA256 under the security audit trail's keys
/// (<see cref="SecurityAuditIntegrity"/>, with a key derived for this store only), over every field of the row but its
/// database id and over every statement it holds (<see cref="McpAuditStatement"/>, as a set: changing, removing, adding or
/// moving a statement to another row breaks the seal). The row carries a guid of its own, unique in the table, so a sealed
/// row cannot be copied into a second one. Rows are sealed as they are saved, after the content retention lock has
/// stripped what it strips (<c>SecurityAuditInterceptor</c>), and verified by <see cref="IMcpAuditVerificationService"/>.
/// The fields are written in a fixed binary form (each value tagged and length-prefixed, text as its UTF-16 code units), so
/// any text a caller sends seals without failing the audit write.
/// </summary>
internal static class McpAuditSeal
{
    /// <summary>The store name the seal key is derived for.</summary>
    internal const string Store = "mcp_audit";

    /// <summary>
    /// The field list new rows are sealed with. A field added to the row later (the server-minted correlation id, for one)
    /// is sealed by raising this version and writing that field for it only, in <see cref="Canonical"/>; rows sealed under
    /// an earlier version keep verifying with the fields they were sealed with.
    /// </summary>
    internal const int CurrentVersion = 1;

    private const byte NullTag = 0;
    private const byte TextTag = 1;
    private const byte IntTag = 2;
    private const byte LongTag = 3;

    /// <summary>
    /// Seals <paramref name="row"/> and the statements it holds: times to the precision both providers keep, a guid when it
    /// has none, the current seal version and key id, and the HMAC.
    /// </summary>
    public static void Seal(McpAuditLog row, SecurityAuditIntegrity integrity)
    {
        row.CreatedTime = SecurityAuditIntegrity.StoredTime(row.CreatedTime);
        foreach (var statement in row.Statements)
        {
            statement.CreatedTime = SecurityAuditIntegrity.StoredTime(statement.CreatedTime);
        }

        row.AuditGuid ??= Guid.NewGuid();
        row.SealVersion = CurrentVersion;
        row.HmacKeyId = integrity.CurrentKeyId;
        row.Hmac = integrity.SealRecord(Store, Canonical(row, row.Statements)).Hmac;
    }

    /// <summary>
    /// How <paramref name="row"/>, read back with <paramref name="statements"/>, verifies: <see cref="McpAuditVerdicts.Unsealed"/>
    /// when it carries no seal at all (written before rows were sealed), <see cref="McpAuditVerdicts.UnknownVersion"/> when its
    /// seal names the next version (a node already on a later version, during an upgrade), else intact, changed (also a seal
    /// that is only partly there or names any other version) or under a key Beacon does not hold.
    /// </summary>
    public static string Verify(McpAuditLog row, IReadOnlyCollection<McpAuditStatement> statements, SecurityAuditIntegrity integrity)
    {
        if (row.AuditGuid == null && row.SealVersion == null && row.HmacKeyId == null && row.Hmac == null)
        {
            return McpAuditVerdicts.Unsealed;
        }

        if (row.AuditGuid != null && row.HmacKeyId != null && row.Hmac != null && row.SealVersion == CurrentVersion + 1)
        {
            return McpAuditVerdicts.UnknownVersion;
        }

        if (row.AuditGuid == null || row.SealVersion != CurrentVersion || row.HmacKeyId == null || row.Hmac == null)
        {
            return SecurityAuditVerdicts.Modified;
        }

        return integrity.VerifyRecord(Store, row.HmacKeyId, row.Hmac, Canonical(row, statements)) switch
        {
            SecurityAuditVerdict.Intact => SecurityAuditVerdicts.Intact,
            SecurityAuditVerdict.UnknownKey => SecurityAuditVerdicts.UnknownKey,
            _ => SecurityAuditVerdicts.Modified
        };
    }

    // Version 1: every field of the row but its database id, in a fixed order, then its statements in a fixed order of
    // their own encodings (the order they are stored or read in does not matter).
    private static byte[] Canonical(McpAuditLog row, IEnumerable<McpAuditStatement> statements)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        Text(writer, "beacon:mcp-audit");
        Int(writer, row.SealVersion);
        Text(writer, row.AuditGuid?.ToString("D"));
        Long(writer, row.CreatedTime.Ticks);
        Int(writer, row.UserId);
        Text(writer, row.Tool);
        Text(writer, row.Parameters);
        Int(writer, row.DataSourceId);
        Int(writer, row.ProjectId);
        Int(writer, row.ExecutionTimeMs);
        Int(writer, row.ResultRowCount);
        Text(writer, row.ErrorMessage);
        Text(writer, row.CallerKind);
        Text(writer, row.CallerHash);
        Text(writer, row.TraceId);
        Text(writer, row.SpanId);
        Text(writer, row.McpSessionId);
        Text(writer, row.UpstreamRequestId);
        Int(writer, row.ApiKeyId);
        Text(writer, row.ContentHash);
        Text(writer, row.HmacKeyId);

        var encoded = statements
            .Select(Canonical)
            .ToList();
        encoded.Sort((x, y) => x.AsSpan().SequenceCompareTo(y));
        writer.Write(encoded.Count);
        foreach (var statement in encoded)
        {
            writer.Write(statement.Length);
            writer.Write(statement);
        }

        writer.Flush();

        return stream.ToArray();
    }

    private static byte[] Canonical(McpAuditStatement statement)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        Long(writer, statement.CreatedTime.Ticks);
        Int(writer, statement.ProjectId);
        Int(writer, statement.DataSourceId);
        Text(writer, statement.Purpose);
        Text(writer, statement.SqlHash);
        Text(writer, statement.Sql);
        Text(writer, statement.TablesJson);
        Int(writer, statement.RowCount);
        Int(writer, statement.DurationMs);
        Text(writer, statement.ErrorClass);
        writer.Flush();

        return stream.ToArray();
    }

    // Text as its UTF-16 code units, not an encoding: a value no encoder accepts (an unpaired surrogate) seals as it is.
    private static void Text(BinaryWriter writer, string? value)
    {
        if (value == null)
        {
            writer.Write(NullTag);

            return;
        }

        writer.Write(TextTag);
        writer.Write(value.Length);
        foreach (var unit in value)
        {
            writer.Write((ushort)unit);
        }
    }

    private static void Int(BinaryWriter writer, int? value)
    {
        if (value == null)
        {
            writer.Write(NullTag);

            return;
        }

        writer.Write(IntTag);
        writer.Write(value.Value);
    }

    private static void Long(BinaryWriter writer, long value)
    {
        writer.Write(LongTag);
        writer.Write(value);
    }
}

/// <summary>What verification says about an MCP audit row, beside the <see cref="SecurityAuditVerdicts"/> it shares.</summary>
public static class McpAuditVerdicts
{
    /// <summary>
    /// The row carries no seal: it was written before rows were sealed (or, during an upgrade, by a node still running the
    /// version before). Never reported as changed; a row without a seal written after sealing started is.
    /// </summary>
    public const string Unsealed = "unsealed";

    /// <summary>
    /// The row's seal names the version after the one this node writes: sealed by a node already running a later version (an
    /// upgrade in progress). Reported at Warning, never as changed and never as intact; it verifies once the node running
    /// the verification runs that version, so one that stays after the upgrade is a finding.
    /// </summary>
    public const string UnknownVersion = "unknown_version";
}
