namespace Beacon.Core.Data.Enums;

public enum MigrationMode
{
    Insert = 1,        // Insert new rows only
    Upsert = 2,        // Insert or update based on key
    Truncate = 3       // Truncate destination before insert
}