namespace Semantico.Core.Data.Enums;

public enum SchemaChangeType
{
    TableAdded,
    TableDropped,
    ColumnAdded,
    ColumnDropped,
    ColumnTypeChanged,
    ColumnDefaultChanged,
    IndexAdded,
    IndexDropped,
    ConstraintAdded,
    ConstraintDropped
}
