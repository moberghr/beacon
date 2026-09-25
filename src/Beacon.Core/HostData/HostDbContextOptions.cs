using Beacon.Core.Data.Enums;

namespace Beacon.Core.HostData;

/// <summary>
/// Configures how a host application's own EF Core <c>DbContext</c> is exposed as a Beacon data source.
/// Tables are DEFAULT DENY: only tables named through <see cref="AllowTables"/> / <see cref="AllowEntity{TEntity}"/>
/// (or every table after an explicit <see cref="AllowAllTables"/>) are visible to Beacon and queryable.
/// </summary>
public sealed class HostDbContextOptions
{
    private readonly List<string> _allowedTables = [];
    private readonly List<Type> _allowedEntityTypes = [];
    private readonly List<Func<HostColumn, bool>> _excludePredicates = [];
    private readonly List<Func<HostColumn, bool>> _maskPredicates = [];
    private readonly HashSet<string> _secretLikeOverrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Data-source name shown in Beacon. Defaults to the context type name.</summary>
    public string? Name { get; set; }

    /// <summary>Project the data source is created in / attached to. Defaults to <see cref="Name"/>.</summary>
    public string? ProjectName { get; set; }

    /// <summary>Optional project description used when the project is created.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// REQUIRED. Name of the entry under <c>ConnectionStrings</c> holding a dedicated read-only login. The string
    /// itself is resolved from configuration at execution time and is never persisted.
    /// </summary>
    public string? ReadOnlyConnectionStringName { get; set; }

    /// <summary>
    /// Engine override. Normally left null — the engine is inferred from the context's EF provider
    /// (SQL Server or PostgreSQL). Any other provider is rejected.
    /// </summary>
    public DatabaseEngineType? Engine { get; set; }

    internal IReadOnlyList<string> AllowedTables => _allowedTables;

    internal IReadOnlyList<Type> AllowedEntityTypes => _allowedEntityTypes;

    internal bool AllowAll { get; private set; }

    internal IReadOnlyList<Func<HostColumn, bool>> ExcludePredicates => _excludePredicates;

    internal IReadOnlyList<Func<HostColumn, bool>> MaskPredicates => _maskPredicates;

    internal IReadOnlySet<string> SecretLikeOverrides => _secretLikeOverrides;

    /// <summary>Sets <see cref="ReadOnlyConnectionStringName"/>.</summary>
    public HostDbContextOptions ReadOnlyConnection(string connectionStringName)
    {
        ReadOnlyConnectionStringName = connectionStringName;
        return this;
    }

    /// <summary>Allow-lists tables (or views) by store name, either <c>Table</c> or <c>schema.Table</c> (case-insensitive).</summary>
    public HostDbContextOptions AllowTables(params string[] tableNames)
    {
        foreach (var tableName in tableNames)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                throw new ArgumentException("Allow-listed table names must not be empty.", nameof(tableNames));
            }

            _allowedTables.Add(tableName.Trim());
        }

        return this;
    }

    /// <summary>Allow-lists the table (or view) the entity type is mapped to.</summary>
    public HostDbContextOptions AllowEntity<TEntity>() where TEntity : class
    {
        _allowedEntityTypes.Add(typeof(TEntity));
        return this;
    }

    /// <summary>
    /// Exposes EVERY table and view in the model. Deliberately explicit — prefer <see cref="AllowTables"/>.
    /// Column exclusions, masking and the secret-like hard-exclude still apply.
    /// </summary>
    public HostDbContextOptions AllowAllTables()
    {
        AllowAll = true;
        return this;
    }

    /// <summary>Columns matching the predicate are invisible in metadata AND rejected at execution.</summary>
    public HostDbContextOptions ExcludeColumns(Func<HostColumn, bool> predicate)
    {
        _excludePredicates.Add(predicate);
        return this;
    }

    /// <summary>
    /// Columns matching the predicate stay visible in metadata, flagged as PII, and their values are masked in
    /// every query result. Exclusion wins over masking.
    /// </summary>
    public HostDbContextOptions MaskColumns(Func<HostColumn, bool> predicate)
    {
        _maskPredicates.Add(predicate);
        return this;
    }

    /// <summary>
    /// DANGER: lifts the built-in secret-like hard-exclude (see <see cref="SecretLikeColumnNames"/>) for ONE column,
    /// given as <c>Table.Column</c> or <c>schema.Table.Column</c>. Only use it for a column whose name merely looks
    /// secret (for example <c>TokenCount</c>). <see cref="ExcludeColumns"/> predicates still apply.
    /// </summary>
    public HostDbContextOptions IncludeSecretLikeColumn(string tableDotColumn)
    {
        if (string.IsNullOrWhiteSpace(tableDotColumn) || !tableDotColumn.Contains('.'))
        {
            throw new ArgumentException("Use the form 'Table.Column' or 'schema.Table.Column'.", nameof(tableDotColumn));
        }

        _secretLikeOverrides.Add(tableDotColumn.Trim());
        return this;
    }
}

/// <summary>
/// One mapped column of the host model, handed to <see cref="HostDbContextOptions.ExcludeColumns"/> and
/// <see cref="HostDbContextOptions.MaskColumns"/> predicates.
/// </summary>
/// <param name="Schema">Store schema (the provider default when the model sets none).</param>
/// <param name="Table">Store table or view name.</param>
/// <param name="Name">Store column name.</param>
/// <param name="ClrType">CLR type of the mapped property.</param>
/// <param name="EntityType">CLR type of the entity declaring the property.</param>
/// <param name="PropertyName">EF property name (differs from <paramref name="Name"/> when the column is renamed).</param>
public sealed record HostColumn(
    string Schema,
    string Table,
    string Name,
    Type ClrType,
    Type EntityType,
    string PropertyName)
{
    /// <summary>Alias of <see cref="Name"/>.</summary>
    public string Column => Name;
}
