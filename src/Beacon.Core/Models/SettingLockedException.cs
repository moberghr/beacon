namespace Beacon.Core.Models;

/// <summary>
/// A write tried to change a setting that a deployment lock (<c>Beacon:Mcp:Force*</c>) pins. Mapped to HTTP 409
/// by the API exception middleware — the caller's request is valid, it just conflicts with a deployment fact.
/// </summary>
public sealed class SettingLockedException(string fieldName, string message) : BeaconException(message)
{
    public string FieldName { get; } = fieldName;

    public static SettingLockedException For(string fieldName, string lockName)
    {
        return new SettingLockedException(
            fieldName,
            $"Setting '{fieldName}' is pinned by the deployment lock '{lockName}' and cannot be changed here.");
    }
}
