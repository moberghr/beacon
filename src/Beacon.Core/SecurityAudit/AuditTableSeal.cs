using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// The seal of a row of an audit table beside the MCP audit (<see cref="AuditTableStores"/>: the execution log and the
/// settings history): a keyed HMAC-SHA256 under the security audit trail's keys (<see cref="SecurityAuditIntegrity"/>, with
/// a key derived for that table only), over every field of the row but its database id. The row carries a guid of its own,
/// unique in its table, so a sealed row cannot be copied into a second one. Rows are sealed as they are saved
/// (<c>SecurityAuditInterceptor</c>) and verified by <see cref="IAuditTableVerificationService"/>. The fields are written
/// in a fixed binary form (each value tagged and length-prefixed, text as its UTF-16 code units), as the MCP audit seal
/// writes them (<see cref="McpAuditSeal"/>).
/// </summary>
internal static class AuditTableSeal
{
    /// <summary>
    /// The field list new rows are sealed with. A field added to a table later is sealed by raising this version and writing
    /// that field for it only; rows sealed under an earlier version keep verifying with the fields they were sealed with.
    /// </summary>
    internal const int CurrentVersion = 1;

    /// <summary>
    /// Seals <paramref name="row"/>: times to the precision both providers keep, a guid when it has none, the current seal
    /// version and key id, and the HMAC.
    /// </summary>
    public static void Seal<T>(T row, AuditTableStore<T> store, SecurityAuditIntegrity integrity)
        where T : BaseEntity, ISealedAuditRow
    {
        row.CreatedTime = SecurityAuditIntegrity.StoredTime(row.CreatedTime);
        store.NormalizeTimes?.Invoke(row);
        row.AuditGuid ??= Guid.NewGuid();
        row.SealVersion = CurrentVersion;
        row.HmacKeyId = integrity.CurrentKeyId;
        row.Hmac = integrity.SealRecord(store.Name, Canonical(row, store)).Hmac;
    }

    /// <summary>
    /// How <paramref name="row"/> verifies: <see cref="McpAuditVerdicts.Unsealed"/> when it carries no seal at all (written
    /// before rows were sealed), <see cref="McpAuditVerdicts.UnknownVersion"/> when its seal names the next version (a node
    /// already on a later version, during an upgrade), else intact, changed (also a seal that is only partly there or names
    /// any other version) or under a key Beacon does not hold.
    /// </summary>
    public static string Verify<T>(T row, AuditTableStore<T> store, SecurityAuditIntegrity integrity)
        where T : BaseEntity, ISealedAuditRow
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

        return integrity.VerifyRecord(store.Name, row.HmacKeyId, row.Hmac, Canonical(row, store)) switch
        {
            SecurityAuditVerdict.Intact => SecurityAuditVerdicts.Intact,
            SecurityAuditVerdict.UnknownKey => SecurityAuditVerdicts.UnknownKey,
            _ => SecurityAuditVerdicts.Modified
        };
    }

    // Version 1: the table's label, the seal's own fields, the row's time, then the table's fields in a fixed order and the
    // key id.
    private static byte[] Canonical<T>(T row, AuditTableStore<T> store)
        where T : BaseEntity, ISealedAuditRow
    {
        using var stream = new MemoryStream();
        using var writer = new SealWriter(stream);
        writer.Text(store.Label);
        writer.Int(row.SealVersion);
        writer.Text(row.AuditGuid?.ToString("D"));
        writer.Long(row.CreatedTime.Ticks);
        store.WriteFields(writer, row);
        writer.Text(row.HmacKeyId);
        writer.Flush();

        return stream.ToArray();
    }
}

/// <summary>
/// One sealed audit table: the name its seal key is derived for, the label its canonical form starts with, its fields, how
/// it is read, the security audit actions its verification and its sealing start are recorded under, and the resource type
/// they name.
/// </summary>
internal sealed class AuditTableStore<T>(
    string name,
    string label,
    Action<SealWriter, T> writeFields,
    Func<BeaconContext, IQueryable<T>> rows,
    string verifiedAction,
    string sealingStartedAction,
    string resource,
    Action<T>? normalizeTimes = null)
    where T : BaseEntity, ISealedAuditRow
{
    public string Name => name;

    public string Label => label;

    public Action<SealWriter, T> WriteFields => writeFields;

    public Func<BeaconContext, IQueryable<T>> Rows => rows;

    public string VerifiedAction => verifiedAction;

    public string SealingStartedAction => sealingStartedAction;

    public string Resource => resource;

    /// <summary>Brings the row's other times to the precision both providers keep, before it is sealed.</summary>
    public Action<T>? NormalizeTimes => normalizeTimes;
}

/// <summary>The sealed audit tables beside the security audit trail and the MCP audit.</summary>
internal static class AuditTableStores
{
    /// <summary>
    /// The execution log (<see cref="ManualQueryExecutionLog"/>): every field but the database id and the data source
    /// navigation.
    /// </summary>
    public static readonly AuditTableStore<ManualQueryExecutionLog> ExecutionLog = new(
        "execution_log",
        "beacon:execution-log",
        (writer, row) =>
        {
            writer.Text(row.UserId);
            writer.Int(row.TriggeredByUserId);
            writer.Int((int?)row.Status);
            writer.Time(row.CompletedTime);
            writer.Text(row.QueryText);
            writer.Int(row.ResultCount);
            writer.Double(row.ExecutionTimeMs);
            writer.Int(row.DataSourceId);
            writer.Text(row.ExecutionContext);
            writer.Bool(row.Success);
            writer.Text(row.ErrorMessage);
        },
        x => x.ManualQueryExecutionLogs,
        SecurityAuditActions.ExecutionLogVerified,
        SecurityAuditActions.ExecutionLogSealingStarted,
        SecurityAuditResources.ExecutionLog,
        x => x.CompletedTime = x.CompletedTime is { } completed ? SecurityAuditIntegrity.StoredTime(completed) : null);

    /// <summary>The settings history (<see cref="AppSettingHistory"/>): every field but the database id.</summary>
    public static readonly AuditTableStore<AppSettingHistory> SettingHistory = new(
        "setting_history",
        "beacon:setting-history",
        (writer, row) =>
        {
            writer.Text(row.SettingKey);
            writer.Text(row.OldValue);
            writer.Text(row.NewValue);
            writer.Long(row.ChangedAt.Ticks);
            writer.Text(row.ChangedByUserId);
        },
        x => x.AppSettingHistory,
        SecurityAuditActions.SettingHistoryVerified,
        SecurityAuditActions.SettingHistorySealingStarted,
        SecurityAuditResources.Settings,
        x => x.ChangedAt = SecurityAuditIntegrity.StoredTime(x.ChangedAt));
}

/// <summary>
/// The fixed binary form a seal is computed over: each value tagged (absent, text, number) and text length-prefixed as its
/// UTF-16 code units, not an encoding, so a value no encoder accepts (an unpaired surrogate) seals as it is.
/// </summary>
internal sealed class SealWriter(Stream stream) : IDisposable
{
    private const byte NullTag = 0;
    private const byte TextTag = 1;
    private const byte IntTag = 2;
    private const byte LongTag = 3;
    private const byte DoubleTag = 4;
    private const byte BoolTag = 5;

    private readonly BinaryWriter _writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

    public void Text(string? value)
    {
        if (value == null)
        {
            _writer.Write(NullTag);

            return;
        }

        _writer.Write(TextTag);
        _writer.Write(value.Length);
        foreach (var unit in value)
        {
            _writer.Write((ushort)unit);
        }
    }

    public void Int(int? value)
    {
        if (value == null)
        {
            _writer.Write(NullTag);

            return;
        }

        _writer.Write(IntTag);
        _writer.Write(value.Value);
    }

    public void Long(long value)
    {
        _writer.Write(LongTag);
        _writer.Write(value);
    }

    public void Time(DateTime? value)
    {
        if (value == null)
        {
            _writer.Write(NullTag);

            return;
        }

        Long(value.Value.Ticks);
    }

    // The value's bits: both providers store a double as the same 64 bits.
    public void Double(double value)
    {
        _writer.Write(DoubleTag);
        _writer.Write(BitConverter.DoubleToInt64Bits(value));
    }

    public void Bool(bool value)
    {
        _writer.Write(BoolTag);
        _writer.Write(value);
    }

    public void Flush()
    {
        _writer.Flush();
    }

    public void Dispose()
    {
        _writer.Dispose();
    }
}
