using System.Dynamic;
using FluentAssertions;
using NUnit.Framework;
using Beacon.Connector.AzureSynapse;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.Snowflake;
using Beacon.Connector.SqlServer;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;

namespace Beacon.Tests.Unit.Metadata;

/// <summary>
/// Characterization of what each metadata extractor builds from the rows its catalog queries return (the pure
/// <c>Assemble</c> step; the catalog SQL itself is untouched and has no connection here). The fixtures describe the catalog
/// in engine-neutral rows and each engine adapter turns them into the row types that engine's driver returns.
/// </summary>
[TestFixture]
public class MetadataExtractorAssembleTests
{
    private sealed record Col(string Schema, string Table, string Name, string Type, bool Nullable, bool Pk, int Ordinal, int? MaxLength = null, string? Default = null);

    private sealed record Fk(string Schema, string Table, string Column, string ForeignSchema, string ForeignTable, string ForeignColumn, string Constraint);

    private sealed record Idx(string Schema, string Table, string Name, bool Unique, string[] Columns);

    public static readonly string[] ForeignKeyEngines = ["SqlServer", "Synapse", "PostgreSql", "MySql"];

    private static IReadOnlyList<TableMetadataDto> Assemble(string engine, Col[] columns, Fk[] foreignKeys, Idx[] indexes)
    {
        return engine switch
        {
            "SqlServer" => SqlServerMetadataExtractor.Assemble(
                columns.Select(x => Row(("table_schema", x.Schema), ("table_name", x.Table), ("column_name", x.Name), ("data_type", x.Type), ("is_nullable", x.Nullable), ("max_length", x.MaxLength is null ? null : (short)x.MaxLength), ("ordinal_position", x.Ordinal), ("is_primary_key", x.Pk ? 1 : 0))),
                ForeignKeyRows(foreignKeys),
                indexes.Select(x => Row(("table_schema", x.Schema), ("table_name", x.Table), ("index_name", x.Name), ("is_unique", x.Unique), ("is_primary_key", false), ("column_names", string.Join(',', x.Columns))))),
            "Synapse" => AzureSynapseMetadataExtractor.Assemble(
                columns.Select(x => Row(("table_schema", x.Schema), ("table_name", x.Table), ("column_name", x.Name), ("data_type", x.Type), ("is_nullable", x.Nullable), ("max_length", x.MaxLength is null ? null : (short)x.MaxLength), ("ordinal_position", x.Ordinal), ("is_primary_key", x.Pk ? 1 : 0))),
                ForeignKeyRows(foreignKeys),
                indexes.SelectMany(x => x.Columns.Select((y, i) => Row(("table_schema", x.Schema), ("table_name", x.Table), ("index_name", x.Name), ("is_unique", x.Unique), ("is_primary_key", false), ("column_name", y), ("key_ordinal", i + 1))))),
            "PostgreSql" => PostgreSqlMetadataExtractor.Assemble(
                columns.Select(x => Row(("table_schema", x.Schema), ("table_name", x.Table), ("column_name", x.Name), ("data_type", x.Type), ("is_nullable", x.Nullable ? "YES" : "NO"), ("column_default", x.Default), ("character_maximum_length", x.MaxLength), ("ordinal_position", (short)x.Ordinal), ("is_primary_key", x.Pk))),
                ForeignKeyRows(foreignKeys),
                indexes.Select(x => Row(("table_schema", x.Schema), ("table_name", x.Table), ("index_name", x.Name), ("indexdef", $"CREATE {(x.Unique ? "UNIQUE " : "")}INDEX {x.Name} ON {x.Schema}.{x.Table} USING btree ({string.Join(", ", x.Columns)})")))),
            "MySql" => MySqlMetadataExtractor.Assemble(
                columns.Select(x => Row(("table_schema", x.Schema), ("table_name", x.Table), ("column_name", x.Name), ("data_type", x.Type), ("is_nullable", x.Nullable ? "YES" : "NO"), ("column_default", x.Default), ("character_maximum_length", x.MaxLength is null ? null : (long)x.MaxLength), ("ordinal_position", (uint)x.Ordinal), ("column_key", x.Pk ? "PRI" : ""))),
                ForeignKeyRows(foreignKeys),
                indexes.Select(x => Row(("table_schema", x.Schema), ("table_name", x.Table), ("index_name", x.Name), ("non_unique", x.Unique ? 0L : 1L), ("column_names", string.Join(',', x.Columns))))),
            _ => throw new ArgumentOutOfRangeException(nameof(engine))
        };
    }

    private static IEnumerable<dynamic> ForeignKeyRows(Fk[] foreignKeys)
    {
        return foreignKeys.Select(x => Row(("table_schema", x.Schema), ("table_name", x.Table), ("column_name", x.Column), ("foreign_schema_name", x.ForeignSchema), ("foreign_table_name", x.ForeignTable), ("foreign_column_name", x.ForeignColumn), ("constraint_name", x.Constraint)));
    }

    private static dynamic Row(params (string Key, object? Value)[] values)
    {
        var row = new ExpandoObject();
        var dictionary = (IDictionary<string, object?>)row;
        foreach (var (key, value) in values)
        {
            dictionary[key] = value;
        }

        return row;
    }

    private static Col C(string table, string name, int ordinal, bool pk = false, bool nullable = false, string type = "int", int? maxLength = null, string? @default = null, string schema = "app")
    {
        return new Col(schema, table, name, type, nullable, pk, ordinal, maxLength, @default);
    }

    [TestCaseSource(nameof(ForeignKeyEngines))]
    public void EmptyCatalogYieldsNoTables(string engine)
    {
        Assemble(engine, [], [], []).Should().BeEmpty();
    }

    [TestCaseSource(nameof(ForeignKeyEngines))]
    public void PrimaryKeyColumnIsFlaggedAndOthersAreNot(string engine)
    {
        var tables = Assemble(engine, [C("tenants", "id", 1, pk: true), C("tenants", "name", 2, type: "varchar", maxLength: 100)], [], []);

        var table = tables.Should().ContainSingle().Subject;
        table.SchemaName.Should().Be("app");
        table.TableName.Should().Be("tenants");
        table.Description.Should().BeNull();
        table.Columns.Select(x => (x.ColumnName, x.IsPrimaryKey, x.IsForeignKey)).Should().Equal(("id", true, false), ("name", false, false));
        table.Columns.Select(x => x.OrdinalPosition).Should().Equal(1, 2);
        table.Columns.Should().OnlyContain(x => x.ForeignKeyTable == null && x.ForeignKeyColumn == null && x.ForeignKeySchema == null && x.ForeignKeyConstraintName == null);
    }

    [TestCaseSource(nameof(ForeignKeyEngines))]
    public void CompositePrimaryKeyFlagsEveryKeyColumn(string engine)
    {
        var tables = Assemble(engine, [C("links", "left_id", 1, pk: true), C("links", "right_id", 2, pk: true), C("links", "note", 3, nullable: true, type: "varchar", maxLength: 50)], [], []);

        tables.Single().Columns.Select(x => (x.ColumnName, x.IsPrimaryKey)).Should().Equal(("left_id", true), ("right_id", true), ("note", false));
    }

    [TestCaseSource(nameof(ForeignKeyEngines))]
    public void NullableAndMaxLengthAreCarriedPerColumn(string engine)
    {
        var tables = Assemble(engine, [C("people", "id", 1, pk: true), C("people", "nickname", 2, nullable: true, type: "varchar", maxLength: 40), C("people", "age", 3, nullable: true)], [], []);

        var columns = tables.Single().Columns;
        columns.Select(x => x.IsNullable).Should().Equal(false, true, true);
        columns.Select(x => x.MaxLength).Should().Equal(null, 40, null);
    }

    [TestCaseSource(nameof(ForeignKeyEngines))]
    public void SingleForeignKeyNamesTargetSchemaTableColumnAndConstraint(string engine)
    {
        var tables = Assemble(
            engine,
            [C("tenants", "id", 1, pk: true), C("users", "id", 1, pk: true), C("users", "tenant_id", 2)],
            [new Fk("app", "users", "tenant_id", "app", "tenants", "id", "fk_users_tenants")],
            []);

        tables.Select(x => x.TableName).Should().Equal("tenants", "users");
        var column = tables[1].Columns.Single(x => x.ColumnName == "tenant_id");
        column.IsForeignKey.Should().BeTrue();
        column.ForeignKeySchema.Should().Be("app");
        column.ForeignKeyTable.Should().Be("tenants");
        column.ForeignKeyColumn.Should().Be("id");
        column.ForeignKeyConstraintName.Should().Be("fk_users_tenants");
        tables[1].Columns.Single(x => x.ColumnName == "id").IsForeignKey.Should().BeFalse();
    }

    [TestCaseSource(nameof(ForeignKeyEngines))]
    public void TwoForeignKeysOnOneColumnKeepExactlyOneTarget(string engine)
    {
        // Today the extractor keeps the first row of the column's group (g.First()), and the catalog queries have no ORDER BY,
        // so which target survives is not deterministic on a real database. This pins "one of the valid targets, all fields from
        // the same constraint". A later change that carries every foreign key replaces this fixture on purpose.
        var tables = Assemble(
            engine,
            [C("tenants", "id", 1, pk: true), C("orgs", "id", 1, pk: true), C("users", "tenant_id", 1)],
            [
                new Fk("app", "users", "tenant_id", "app", "tenants", "id", "fk_users_tenants"),
                new Fk("app", "users", "tenant_id", "app", "orgs", "id", "fk_users_orgs")
            ],
            []);

        var column = tables.Single(x => x.TableName == "users").Columns.Single();
        column.IsForeignKey.Should().BeTrue();
        column.ForeignKeyTable.Should().BeOneOf("tenants", "orgs");
        column.ForeignKeyConstraintName.Should().Be(column.ForeignKeyTable == "tenants" ? "fk_users_tenants" : "fk_users_orgs");
        column.ForeignKeyColumn.Should().Be("id");
        column.ForeignKeySchema.Should().Be("app");
    }

    [TestCaseSource(nameof(ForeignKeyEngines))]
    public void CompositeForeignKeyMapsEachColumnToItsPairedTargetColumn(string engine)
    {
        var tables = Assemble(
            engine,
            [C("x", "tenant_id", 1, pk: true), C("x", "id", 2, pk: true), C("orders", "tenant_id", 1), C("orders", "x_id", 2)],
            [
                new Fk("app", "orders", "tenant_id", "app", "x", "tenant_id", "fk_orders_x"),
                new Fk("app", "orders", "x_id", "app", "x", "id", "fk_orders_x")
            ],
            []);

        var columns = tables.Single(x => x.TableName == "orders").Columns;
        columns.Select(x => (x.ColumnName, x.IsForeignKey, x.ForeignKeyTable, x.ForeignKeyColumn, x.ForeignKeyConstraintName))
            .Should().Equal(("tenant_id", true, "x", "tenant_id", "fk_orders_x"), ("x_id", true, "x", "id", "fk_orders_x"));
    }

    [TestCaseSource(nameof(ForeignKeyEngines))]
    public void SelfReferencingForeignKeyPointsAtItsOwnTable(string engine)
    {
        var tables = Assemble(
            engine,
            [C("employees", "id", 1, pk: true), C("employees", "manager_id", 2, nullable: true)],
            [new Fk("app", "employees", "manager_id", "app", "employees", "id", "fk_employees_manager")],
            []);

        var column = tables.Single().Columns.Single(x => x.ColumnName == "manager_id");
        column.IsForeignKey.Should().BeTrue();
        column.ForeignKeyTable.Should().Be("employees");
        column.ForeignKeyColumn.Should().Be("id");
        column.IsNullable.Should().BeTrue();
    }

    [TestCaseSource(nameof(ForeignKeyEngines))]
    public void ForeignKeyIsMatchedPerSchemaTableAndColumn(string engine)
    {
        var tables = Assemble(
            engine,
            [C("users", "tenant_id", 1, schema: "a"), C("users", "tenant_id", 1, schema: "b")],
            [new Fk("a", "users", "tenant_id", "a", "tenants", "id", "fk_a")],
            []);

        tables.Single(x => x.SchemaName == "a").Columns.Single().IsForeignKey.Should().BeTrue();
        tables.Single(x => x.SchemaName == "b").Columns.Single().IsForeignKey.Should().BeFalse();
    }

    [TestCaseSource(nameof(ForeignKeyEngines))]
    public void IndexesAreAttachedToTheirTableWithColumnsInOrder(string engine)
    {
        var tables = Assemble(
            engine,
            [C("a", "id", 1, pk: true), C("a", "x", 2), C("a", "y", 3), C("b", "id", 1, pk: true)],
            [],
            [
                new Idx("app", "a", "ix_a_x_y", false, ["x", "y"]),
                new Idx("app", "a", "ux_a_x", true, ["x"]),
                new Idx("app", "b", "ix_b_id", false, ["id"])
            ]);

        var a = tables.Single(x => x.TableName == "a").Indexes;
        a.Select(x => x.IndexName).Should().BeEquivalentTo("ix_a_x_y", "ux_a_x");
        a.Single(x => x.IndexName == "ix_a_x_y").Columns.Should().Equal("x", "y");
        a.Single(x => x.IndexName == "ix_a_x_y").IsUnique.Should().BeFalse();
        a.Single(x => x.IndexName == "ux_a_x").IsUnique.Should().BeTrue();
        tables.Single(x => x.TableName == "b").Indexes.Should().ContainSingle().Which.IndexName.Should().Be("ix_b_id");
    }

    [TestCaseSource(nameof(ForeignKeyEngines))]
    public void NamesWithUnsafeCharactersPassThroughAndAreFlaggedByTheIdentifierGuard(string engine)
    {
        // The extractors do not filter names; the metadata refresh leaves out every name the guard flags before storing it.
        var tables = Assemble(
            engine,
            [C("safe", "id", 1, pk: true), C("safe", "bad\"col", 2), C("ev];il", "id", 1, pk: true)],
            [],
            []);

        tables.Select(x => x.TableName).Should().Equal("safe", "ev];il");
        tables.Where(x => SqlIdentifierGuard.HasUnsafeCharacters(x.TableName)).Select(x => x.TableName).Should().Equal("ev];il");
        tables[0].Columns.Where(x => SqlIdentifierGuard.HasUnsafeCharacters(x.ColumnName)).Select(x => x.ColumnName).Should().Equal("bad\"col");
    }

    [Test]
    public void SqlServerSetsIndexPrimaryKeyFlagFromTheRowAndMaxLengthFromTheBoxedShort()
    {
        var tables = SqlServerMetadataExtractor.Assemble(
            [Row(("table_schema", "dbo"), ("table_name", "t"), ("column_name", "name"), ("data_type", "nvarchar"), ("is_nullable", true), ("max_length", (short)200), ("ordinal_position", 1), ("is_primary_key", 0))],
            [],
            [Row(("table_schema", "dbo"), ("table_name", "t"), ("index_name", "pk_t"), ("is_unique", true), ("is_primary_key", true), ("column_names", "a,b"))]);

        var table = tables.Single();
        table.Columns.Single().MaxLength.Should().Be(200);
        table.Indexes.Single().Should().BeEquivalentTo(new IndexMetadataDto("pk_t", true, true, ["a", "b"]));
    }

    [Test]
    public void SynapseOrdersIndexColumnsByKeyOrdinalNotRowOrder()
    {
        var tables = AzureSynapseMetadataExtractor.Assemble(
            [Row(("table_schema", "dbo"), ("table_name", "t"), ("column_name", "a"), ("data_type", "int"), ("is_nullable", false), ("max_length", (short)4), ("ordinal_position", 1), ("is_primary_key", 1))],
            [],
            [
                Row(("table_schema", "dbo"), ("table_name", "t"), ("index_name", "ix"), ("is_unique", false), ("is_primary_key", false), ("column_name", "second"), ("key_ordinal", 2)),
                Row(("table_schema", "dbo"), ("table_name", "t"), ("index_name", "ix"), ("is_unique", false), ("is_primary_key", false), ("column_name", "first"), ("key_ordinal", 1))
            ]);

        tables.Single().Indexes.Should().ContainSingle().Which.Columns.Should().Equal("first", "second");
    }

    [Test]
    public void PostgreSqlReadsIndexFlagsAndColumnsFromTheIndexDefinition()
    {
        var tables = PostgreSqlMetadataExtractor.Assemble(
            [Row(("table_schema", "public"), ("table_name", "t"), ("column_name", "id"), ("data_type", "integer"), ("is_nullable", "NO"), ("column_default", "nextval('t_id_seq'::regclass)"), ("character_maximum_length", null), ("ordinal_position", (short)1), ("is_primary_key", true))],
            [],
            [
                Row(("table_schema", "public"), ("table_name", "t"), ("index_name", "ix_lower"), ("indexdef", "CREATE INDEX ix_lower ON public.t USING btree (lower((name)::text) DESC NULLS LAST, id)")),
                Row(("table_schema", "public"), ("table_name", "t"), ("index_name", "t_pkey"), ("indexdef", "CREATE UNIQUE INDEX t_pkey ON public.t USING btree (id)"))
            ]);

        var table = tables.Single();
        table.Columns.Single().DefaultValue.Should().Be("nextval('t_id_seq'::regclass)");
        table.Indexes.Single(x => x.IndexName == "ix_lower").Columns.Should().Equal("lower((name)::text)", "id");
        // A primary key index's definition does not contain "PRIMARY KEY" text, so the index flag is false today.
        table.Indexes.Single(x => x.IndexName == "t_pkey").Should().BeEquivalentTo(new IndexMetadataDto("t_pkey", true, false, ["id"]));
    }

    [Test]
    public void MySqlFlagsThePrimaryIndexByNameAndKeepsViewColumnsWithoutKeys()
    {
        // INFORMATION_SCHEMA.COLUMNS lists views next to tables, so a view arrives as a table with no key and no index.
        var tables = MySqlMetadataExtractor.Assemble(
            [
                Row(("table_schema", "shop"), ("table_name", "orders"), ("column_name", "id"), ("data_type", "int"), ("is_nullable", "NO"), ("column_default", null), ("character_maximum_length", null), ("ordinal_position", 1u), ("column_key", "PRI")),
                Row(("table_schema", "shop"), ("table_name", "v_orders"), ("column_name", "id"), ("data_type", "int"), ("is_nullable", "YES"), ("column_default", null), ("character_maximum_length", null), ("ordinal_position", 1u), ("column_key", ""))
            ],
            [],
            [Row(("table_schema", "shop"), ("table_name", "orders"), ("index_name", "PRIMARY"), ("non_unique", 0L), ("column_names", "id"))]);

        tables.Single(x => x.TableName == "orders").Indexes.Should().ContainSingle().Which.Should().BeEquivalentTo(new IndexMetadataDto("PRIMARY", true, true, ["id"]));
        var view = tables.Single(x => x.TableName == "v_orders");
        view.Indexes.Should().BeEmpty();
        view.Columns.Single().IsPrimaryKey.Should().BeFalse();
        view.Columns.Single().IsNullable.Should().BeTrue();
    }

    [Test]
    public void SnowflakeFlagsPrimaryKeysOnlyAndNeverReportsForeignKeysOrIndexes()
    {
        var tables = SnowflakeMetadataExtractor.Assemble(
            [
                Row(("table_schema", "PUBLIC"), ("table_name", "LINKS"), ("column_name", "L"), ("data_type", "NUMBER"), ("is_nullable", "NO"), ("column_default", null), ("character_maximum_length", null), ("ordinal_position", 1L)),
                Row(("table_schema", "PUBLIC"), ("table_name", "LINKS"), ("column_name", "R"), ("data_type", "NUMBER"), ("is_nullable", "NO"), ("column_default", null), ("character_maximum_length", null), ("ordinal_position", 2L)),
                Row(("table_schema", "PUBLIC"), ("table_name", "LINKS"), ("column_name", "NOTE"), ("data_type", "TEXT"), ("is_nullable", "YES"), ("column_default", "'x'"), ("character_maximum_length", 16777216L), ("ordinal_position", 3L)),
                Row(("table_schema", "PUBLIC"), ("table_name", "V_LINKS"), ("column_name", "L"), ("data_type", "NUMBER"), ("is_nullable", "YES"), ("column_default", null), ("character_maximum_length", null), ("ordinal_position", 1L))
            ],
            [
                Row(("table_schema", "PUBLIC"), ("table_name", "LINKS"), ("column_name", "L")),
                Row(("table_schema", "PUBLIC"), ("table_name", "LINKS"), ("column_name", "R"))
            ]);

        var links = tables.Single(x => x.TableName == "LINKS");
        links.Columns.Select(x => (x.ColumnName, x.IsPrimaryKey, x.IsNullable)).Should().Equal(("L", true, false), ("R", true, false), ("NOTE", false, true));
        links.Columns.Last().DefaultValue.Should().Be("'x'");
        links.Columns.Last().MaxLength.Should().Be(16777216);
        links.Columns.Should().OnlyContain(x => !x.IsForeignKey && x.ForeignKeyTable == null);
        links.Indexes.Should().BeEmpty();
        tables.Single(x => x.TableName == "V_LINKS").Columns.Single().IsPrimaryKey.Should().BeFalse();
        SnowflakeMetadataExtractor.Assemble([], []).Should().BeEmpty();
    }
}
