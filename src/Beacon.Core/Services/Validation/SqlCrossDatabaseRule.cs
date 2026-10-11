using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// A statement may reach its data source's own database only, with read-only enforcement on or off.
/// <para>
/// A name qualified beyond its schema — SQL Server, PostgreSQL and Snowflake <c>database.schema.object</c>, MySQL and
/// SQLite <c>database.object</c>, Databricks <c>catalog.schema.object</c>, BigQuery <c>project.dataset.object</c> — must
/// carry the database (catalog, project) the data source is configured for (<see cref="DataSourceConfiguredDatabase"/>);
/// unqualified and schema-qualified names are always allowed. A source without a configured database accepts no such
/// qualifier at all, and neither does one whose configured database could not be determined. A name with more parts
/// than the dialect's full form (a T-SQL linked server's <c>server.database.schema.object</c>) is refused, and so is a
/// name resolved only at run time (Snowflake and Databricks <c>IDENTIFIER(...)</c>, Snowflake <c>TABLE('...')</c>) or
/// held in a string (Snowflake <c>GET_DDL</c>, T-SQL <c>OBJECT_DEFINITION</c>). Table, view, function and type names are
/// all checked, wherever they appear in the statement, and so is the table a T-SQL full-text or semantic rowset function
/// (<c>CONTAINSTABLE</c>, <c>FREETEXTTABLE</c>, <c>SEMANTIC*TABLE</c>) reads. A BigQuery name before
/// <c>INFORMATION_SCHEMA</c> is a project unless it is a <c>region-*</c> qualifier.
/// </para>
/// <para>
/// Functions and procedures that reach other servers, files or storage (<see cref="SqlDeniedFunctions.Everywhere"/> and
/// its prefixes: <c>dblink</c>, <c>xp_</c>, <c>lo_</c>, <c>OPENROWSET</c>, <c>OPENQUERY</c>, <c>OPENDATASOURCE</c>,
/// <c>EXTERNAL_QUERY</c>, …) and Databricks file and storage path references are refused here too, so they stay refused
/// when read-only enforcement is off.
/// </para>
/// <para>
/// A statement runs in its data source's own context, so what changes that context is refused: <c>USE</c> in every form
/// (database, catalog, schema, warehouse), role and user switches (<c>SET ROLE</c>, PostgreSQL <c>SET role</c> /
/// <c>session_authorization</c> and <c>set_config</c>, T-SQL <c>EXECUTE AS</c> and <c>sp_setapprole</c>), Databricks
/// settings that name a catalog, and SQLite <c>ATTACH</c>. So is SQL a statement runs from a string, a variable or the
/// session, which no name rule can read: T-SQL <c>EXEC</c> of a variable and the procedures that run SQL text
/// (<c>sp_executesql</c>, <c>sp_sqlexec</c>, …), every <c>EXECUTE</c> and <c>PREPARE</c> outside T-SQL (<c>EXECUTE
/// IMMEDIATE</c>, prepared statements, Snowflake tasks), a function defined from a string, and the PostgreSQL functions
/// that run the query text they are given (<c>query_to_xml</c>, <c>crosstab</c>, <c>ts_stat</c>). Forms the parser cannot
/// read (T-SQL <c>EXEC('...')</c>, PostgreSQL <c>DO</c> blocks, Snowflake <c>USE ROLE</c>, Databricks <c>SET
/// CATALOG</c>, BigQuery <c>SET @@dataset_project_id</c>) are refused as SQL that cannot be parsed. A T-SQL <c>EXEC</c> of
/// a procedure by name stays allowed; its name is checked like any other.
/// </para>
/// <para>
/// SQL that cannot be parsed cannot be checked, so <see cref="Validate"/> refuses it. Comparisons follow the engine:
/// PostgreSQL folds an unquoted name to lower case and Snowflake to upper case, then compares exactly; MySQL compares
/// database names exactly (they are case-sensitive on most servers); SQL Server, SQLite, Databricks and BigQuery compare
/// without regard to case. The rule reads names only: a view, synonym, foreign table, share or stored procedure that
/// itself reaches another database is invisible to it, so the data source login's grants remain the boundary.
/// </para>
/// </summary>
public static class SqlCrossDatabaseRule
{
    private const string InformationSchema = "INFORMATION_SCHEMA";
    private const string BigQueryRegionPrefix = "region-";
    private const string PathReferenceMessage = "File and storage path references (format.`path`) are not allowed. Query a catalog table instead.";

    // T-SQL rowset functions whose first argument is the table they read.
    private static readonly HashSet<string> TSqlTableArgumentFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONTAINSTABLE", "FREETEXTTABLE", "SEMANTICKEYPHRASETABLE", "SEMANTICSIMILARITYTABLE", "SEMANTICSIMILARITYDETAILSTABLE"
    };

    // T-SQL system procedures that run SQL passed as text (or a handle prepared from it), or a command per database.
    private static readonly HashSet<string> TSqlDynamicSqlProcedures = new(StringComparer.OrdinalIgnoreCase)
    {
        "sp_executesql", "sp_sqlexec", "sp_execute", "sp_prepare", "sp_prepexec", "sp_prepexecrpc", "sp_cursoropen",
        "sp_cursorprepare", "sp_cursorprepexec", "sp_cursorexecute", "sp_msforeachdb", "sp_msforeachtable", "sp_msforeach_worker"
    };

    // PostgreSQL functions that run the query text they are given.
    private static readonly HashSet<string> PostgreSqlQueryTextFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "query_to_xml", "query_to_xmlschema", "query_to_xml_and_xmlschema", "crosstab", "crosstab2", "crosstab3", "crosstab4", "ts_stat"
    };

    // PostgreSQL settings that change the role, and so the grants, the SQL runs with.
    private static readonly HashSet<string> PostgreSqlRoleSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "role", "session_authorization"
    };

    /// <summary>The refusal reason, or null when the statement stays in the configured database.</summary>
    /// <param name="sql">The statement as it runs.</param>
    /// <param name="dialect">The data source's dialect name (<see cref="DataSourceSqlDialect"/>).</param>
    /// <param name="configuredDatabase">The database (catalog on Databricks, project on BigQuery) the data source is
    /// configured for; null when it has none and <see cref="DataSourceConfiguredDatabase.Undetermined"/> when it could not
    /// be read, which both allow no qualifier.</param>
    public static string? Validate(string sql, string? dialect, string? configuredDatabase)
    {
        return Check(sql, dialect, configuredDatabase, refuseUnparseable: true);
    }

    /// <summary>
    /// As <see cref="Validate"/>, except that SQL the parser cannot read, or in a dialect the rule does not know, passes:
    /// for checks made when SQL is saved, which leave such SQL to the execution gates (they refuse it).
    /// </summary>
    internal static string? ValidateIfParsed(string sql, string? dialect, string? configuredDatabase)
    {
        return Check(sql, dialect, configuredDatabase, refuseUnparseable: false);
    }

    private static string? Check(string sql, string? dialect, string? configuredDatabase, bool refuseUnparseable)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        var parserDialect = SqlDialects.Resolve(dialect);
        var naming = NamingOf(parserDialect);
        if (naming == null)
        {
            return refuseUnparseable ? "The SQL dialect of this data source is not known, so the databases its SQL names cannot be checked." : null;
        }

        Sequence<Statement> statements;
        try
        {
            statements = SqlAst.Parse(sql, parserDialect);
        }
        catch (SqlAst.TooDeepException)
        {
            return refuseUnparseable ? SqlAst.TooDeepMessage : null;
        }
        catch (Exception)
        {
            return refuseUnparseable ? $"The SQL could not be parsed, so the {naming.Noun}s it names cannot be checked." : null;
        }

        var home = Home.Of(configuredDatabase, naming);
        var functionNames = new HashSet<ObjectName>(ReferenceEqualityComparer.Instance);
        foreach (var statement in statements)
        {
            foreach (var node in SqlAst.Nodes(statement))
            {
                var error = node switch
                {
                    Expression.Function function => CheckFunction(function.Name, ArgumentsOf(function.Args), naming, home, functionNames),
                    TableFactor.Function function => CheckFunction(function.Name, function.Args, naming, home, functionNames),
                    TableFactor.Table { Args: not null } table when naming.HasIdentifierClause && IsIdentifierClause(table.Name) => DynamicNameMessage(naming),
                    TableFactor.Table { Args: not null } table => CheckFunction(table.Name, table.Args.Arguments, naming, home, functionNames),
                    TableFactor.Table table when naming.Dialect is DatabricksDialect && SqlReadOnlyAstValidator.IsPathReference(table.Name) => PathReferenceMessage,
                    TableFactor.TableFunction { Expression: not Expression.Function } => DynamicNameMessage(naming),
                    Statement.Execute execute when SqlDeniedFunctions.ReachesBeyondTheSource(LastPartOf(execute.Name)) => BeyondTheSourceMessage(LastPartOf(execute.Name)),
                    Statement.Execute execute => CheckExecute(execute, naming),
                    Statement.Prepare => DynamicSqlMessage("PREPARE", naming),
                    Statement.Use => $"USE is not allowed: it changes the {naming.Noun}, schema, role or warehouse the SQL after it runs in. Write {naming.LocalForm} names instead.",
                    Statement.SetRole => RoleSwitchMessage("SET ROLE"),
                    Statement.SetVariable setting => CheckSetting(setting, naming),
                    Statement.AttachDatabase => "ATTACH is not allowed: it opens another database.",
                    Statement.CreateFunction { FunctionBody: CreateFunctionBody.AsBeforeOptions { Expression: Expression.LiteralValue } or CreateFunctionBody.AsAfterOptions { Expression: Expression.LiteralValue } } =>
                        $"A function defined from a string is not allowed: the {naming.Noun}s its body names cannot be checked.",
                    ObjectName name when !functionNames.Contains(name) => CheckName(name.Values, naming, home),
                    _ => null
                };

                if (error != null)
                {
                    return error;
                }
            }
        }

        return null;
    }

    private static string? CheckFunction(
        ObjectName name,
        IReadOnlyList<FunctionArg>? arguments,
        Naming naming,
        Home home,
        HashSet<ObjectName> functionNames)
    {
        functionNames.Add(name);
        if (naming.HasIdentifierClause && IsIdentifierClause(name))
        {
            return DynamicNameMessage(naming);
        }

        var lastPart = LastPartOf(name);
        if (SqlDeniedFunctions.ReachesBeyondTheSource(lastPart))
        {
            return BeyondTheSourceMessage(lastPart);
        }

        if (naming.Dialect is PostgreSqlDialect && PostgreSqlQueryTextFunctions.Contains(lastPart))
        {
            return DynamicSqlMessage(lastPart, naming);
        }

        if (naming.Dialect is PostgreSqlDialect && lastPart.Equals("set_config", StringComparison.OrdinalIgnoreCase))
        {
            return "set_config is not allowed: it can change the role, and so the grants, the SQL runs with. Use SET for other settings.";
        }

        if (NamesItsObjectInAString(lastPart, naming.Dialect))
        {
            return $"{lastPart} names its object in a string, so the {naming.Noun} it reads cannot be checked.";
        }

        if (naming.Dialect is MsSqlDialect && TSqlTableArgumentFunctions.Contains(lastPart))
        {
            var tableError = CheckTableArgument(lastPart, arguments, naming, home);
            if (tableError != null)
            {
                return tableError;
            }
        }

        // BigQuery's SAFE. prefix turns a function's errors into NULL; it is not a project.
        var parts = name.Values.ToList();
        if (naming.Dialect is BigQueryDialect && parts.Count > 2 && parts[0].Value.Equals("SAFE", StringComparison.OrdinalIgnoreCase))
        {
            parts.RemoveAt(0);
        }

        return CheckName(parts, naming, home);
    }

    // A T-SQL EXEC of a procedure by name stays allowed: the name is checked like any other. Every other EXECUTE runs SQL
    // the rule cannot read — a string, a variable, a prepared statement, a task — or, as T-SQL EXECUTE AS, switches user.
    private static string? CheckExecute(Statement.Execute execute, Naming naming)
    {
        var parts = execute.Name.Values;
        var lastPart = LastPartOf(execute.Name);
        var keyword = parts.Count == 1 && parts[0].QuoteStyle == null ? lastPart.ToUpperInvariant() : null;
        if (naming.Dialect is not MsSqlDialect)
        {
            var form = keyword is "IMMEDIATE" or "TASK" ? $"EXECUTE {keyword}" : "EXECUTE";

            return DynamicSqlMessage(form, naming);
        }

        if (keyword == "AS")
        {
            return RoleSwitchMessage("EXECUTE AS");
        }

        if (lastPart.StartsWith('@'))
        {
            return DynamicSqlMessage("EXEC of a variable", naming);
        }

        if (TSqlDynamicSqlProcedures.Contains(lastPart))
        {
            return DynamicSqlMessage(lastPart, naming);
        }

        return lastPart.Equals("sp_setapprole", StringComparison.OrdinalIgnoreCase)
            ? RoleSwitchMessage(lastPart)
            : null;
    }

    // Settings that choose the role (PostgreSQL) or the catalog (Databricks: the default catalog, a catalog plugin)
    // change what the SQL after them reaches. Other settings stay allowed.
    private static string? CheckSetting(Statement.SetVariable setting, Naming naming)
    {
        var names = SqlAst.Nodes(setting.Variables)
            .OfType<ObjectName>()
            .Select(x => string.Join('.', x.Values.Select(y => y.Value)));

        foreach (var name in names)
        {
            if (naming.Dialect is PostgreSqlDialect && PostgreSqlRoleSettings.Contains(name))
            {
                return RoleSwitchMessage($"SET {name}");
            }

            if (naming.Dialect is DatabricksDialect && name.Contains("catalog", StringComparison.OrdinalIgnoreCase))
            {
                return $"SET {name} is not allowed: it changes the catalog the SQL runs in, or adds one.";
            }
        }

        return null;
    }

    // The table a full-text or semantic rowset function reads is its first argument, written as a name.
    private static string? CheckTableArgument(string function, IReadOnlyList<FunctionArg>? arguments, Naming naming, Home home)
    {
        var first = arguments is { Count: > 0 } ? arguments[0] : null;
        var expression = first switch
        {
            FunctionArg.Unnamed { FunctionArgExpression: FunctionArgExpression.FunctionExpression x } => x.Expression,
            FunctionArg.Named { Arg: FunctionArgExpression.FunctionExpression x } => x.Expression,
            _ => null
        };

        return expression switch
        {
            Expression.Identifier => null,
            Expression.CompoundIdentifier compound => CheckName(compound.Idents, naming, home),
            _ => $"{function} must name its table directly, so the {naming.Noun} it reads can be checked."
        };
    }

    private static string? CheckName(IReadOnlyList<Ident> parts, Naming naming, Home home)
    {
        if (naming.Dialect is BigQueryDialect && parts.Count >= 2 && IsInformationSchema(parts[^2]))
        {
            return CheckBigQueryInformationSchema(parts, naming, home);
        }

        if (parts.Count <= naming.LocalParts)
        {
            return null;
        }

        if (parts.Count > naming.LocalParts + 1)
        {
            return TooManyPartsMessage(naming);
        }

        return home.Check(parts[0], naming);
    }

    // A BigQuery INFORMATION_SCHEMA view takes a dataset or a region qualifier before it, and a project before that:
    // [project.](dataset|region-x).INFORMATION_SCHEMA.view. A single part before it is read as a project (as in
    // project.INFORMATION_SCHEMA.SCHEMATA) unless it is a region qualifier, so the dataset form has to name the project.
    private static string? CheckBigQueryInformationSchema(IReadOnlyList<Ident> parts, Naming naming, Home home)
    {
        if (parts.Count == 2 || (parts.Count == 3 && IsBigQueryRegion(parts[0])))
        {
            return null;
        }

        if (parts.Count > 4)
        {
            return TooManyPartsMessage(naming);
        }

        var error = home.Check(parts[0], naming);

        return error != null && parts.Count == 3
            ? $"{error} A name before INFORMATION_SCHEMA is read as a project unless it is a region-* qualifier: write project.dataset.INFORMATION_SCHEMA.view."
            : error;
    }

    private static string LastPartOf(ObjectName name)
    {
        return name.Values.Count > 0 ? name.Values[^1].Value : "";
    }

    private static string BeyondTheSourceMessage(string function)
    {
        return $"{function} is not allowed: it reaches other servers, files or storage.";
    }

    private static string DynamicSqlMessage(string form, Naming naming)
    {
        return $"{form} is not allowed: it runs SQL held in a string, a variable or the session, so the {naming.Noun}s that SQL names cannot be checked.";
    }

    private static string RoleSwitchMessage(string form)
    {
        return $"{form} is not allowed: it changes the role or user, and so the grants, the SQL runs with.";
    }

    private static string TooManyPartsMessage(Naming naming)
    {
        return $"The SQL uses a name with more parts than {naming.FullForm}, which can reach another server or {naming.Noun}.";
    }

    private static bool IsInformationSchema(Ident part)
    {
        return part.Value.Equals(InformationSchema, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBigQueryRegion(Ident part)
    {
        return part.Value.StartsWith(BigQueryRegionPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIdentifierClause(ObjectName name)
    {
        return name.Values.Count == 1 && name.Values[0].Value.Equals("IDENTIFIER", StringComparison.OrdinalIgnoreCase);
    }

    // Snowflake's GET_DDL('table', 'db.schema.t'), T-SQL's OBJECT_DEFINITION(OBJECT_ID('db.dbo.p')) and PostgreSQL
    // tablefunc's connectby('table', ...) read an object named in a string. (OBJECT_ID alone returns only a number.)
    private static bool NamesItsObjectInAString(string function, Dialect dialect)
    {
        return dialect switch
        {
            SnowflakeDialect => function.Equals("GET_DDL", StringComparison.OrdinalIgnoreCase),
            MsSqlDialect => function.Equals("OBJECT_DEFINITION", StringComparison.OrdinalIgnoreCase),
            PostgreSqlDialect => function.Equals("connectby", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static IReadOnlyList<FunctionArg>? ArgumentsOf(FunctionArguments? arguments)
    {
        return arguments is FunctionArguments.List list ? list.ArgumentList.Args : null;
    }

    private static string DynamicNameMessage(Naming naming)
    {
        return $"IDENTIFIER(...) and TABLE('...') resolve a name only when the statement runs, so its {naming.Noun} cannot be checked. Write the name directly.";
    }

    private static Naming? NamingOf(Dialect dialect)
    {
        return dialect switch
        {
            PostgreSqlDialect => new Naming(dialect, "database", 2, "schema.table", "database.schema.table", CaseFold.Lower, HasIdentifierClause: false),
            MsSqlDialect => new Naming(dialect, "database", 2, "schema.table", "database.schema.table", CaseFold.Insensitive, HasIdentifierClause: false),
            MySqlDialect => new Naming(dialect, "database", 1, "table", "database.table", CaseFold.Exact, HasIdentifierClause: false),
            SQLiteDialect => new Naming(dialect, "database", 1, "table", "database.table", CaseFold.Insensitive, HasIdentifierClause: false),
            SnowflakeDialect => new Naming(dialect, "database", 2, "schema.table", "database.schema.table", CaseFold.Upper, HasIdentifierClause: true),
            DatabricksDialect => new Naming(dialect, "catalog", 2, "schema.table", "catalog.schema.table", CaseFold.Insensitive, HasIdentifierClause: true),
            BigQueryDialect => new Naming(dialect, "project", 2, "dataset.table", "project.dataset.table", CaseFold.Insensitive, HasIdentifierClause: false),
            _ => null
        };
    }

    private enum CaseFold
    {
        /// <summary>Compared without regard to case.</summary>
        Insensitive,

        /// <summary>Compared exactly, quoted or not.</summary>
        Exact,

        /// <summary>An unquoted name is folded to lower case (PostgreSQL), then compared exactly.</summary>
        Lower,

        /// <summary>An unquoted name is folded to upper case (Snowflake), then compared exactly.</summary>
        Upper
    }

    /// <param name="LocalParts">Parts a name may have without a database: schema and object (MySQL, SQLite: the object only).</param>
    private sealed record Naming(
        Dialect Dialect,
        string Noun,
        int LocalParts,
        string LocalForm,
        string FullForm,
        CaseFold Fold,
        bool HasIdentifierClause)
    {
        // The configured name as the engine resolves it: Snowflake reads an unquoted connection value as an identifier.
        public string CanonicalConfigured(string configured)
        {
            if (Fold != CaseFold.Upper)
            {
                return configured;
            }

            return configured.Length > 1 && configured.StartsWith('"') && configured.EndsWith('"')
                ? configured[1..^1]
                : configured.ToUpperInvariant();
        }

        public bool Matches(Ident qualifier, string home)
        {
            var quoted = qualifier.QuoteStyle != null;

            return Fold switch
            {
                CaseFold.Lower => string.Equals(quoted ? qualifier.Value : qualifier.Value.ToLowerInvariant(), home, StringComparison.Ordinal),
                CaseFold.Upper => string.Equals(quoted ? qualifier.Value : qualifier.Value.ToUpperInvariant(), home, StringComparison.Ordinal),
                CaseFold.Exact => string.Equals(qualifier.Value, home, StringComparison.Ordinal),
                _ => string.Equals(qualifier.Value, home, StringComparison.OrdinalIgnoreCase)
            };
        }
    }

    // The database a qualifier must name: the configured one, none, or one that could not be determined.
    private sealed record Home(string? Name, bool Undetermined)
    {
        public static Home Of(string? configuredDatabase, Naming naming)
        {
            if (DataSourceConfiguredDatabase.IsUndetermined(configuredDatabase))
            {
                return new Home(null, Undetermined: true);
            }

            return string.IsNullOrWhiteSpace(configuredDatabase)
                ? new Home(null, Undetermined: false)
                : new Home(naming.CanonicalConfigured(configuredDatabase.Trim()), Undetermined: false);
        }

        public string? Check(Ident qualifier, Naming naming)
        {
            if (Undetermined)
            {
                return $"The SQL qualifies a name with a {naming.Noun}, but the data source's {naming.Noun} could not be determined (its connection settings could not be read), so only {naming.LocalForm} names are allowed.";
            }

            if (Name == null)
            {
                return $"The SQL qualifies a name with a {naming.Noun}, but this data source has no {naming.Noun} configured, so only {naming.LocalForm} names are allowed.";
            }

            return naming.Matches(qualifier, Name)
                ? null
                : $"The SQL names an object in another {naming.Noun}: only this data source's own {naming.Noun} may qualify a name. Use {naming.LocalForm} names.";
        }
    }
}
