using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabaseMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "database_metadata",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    schema_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    table_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    table_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_refreshed = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_database_metadata", x => x.id);
                    table.ForeignKey(
                        name: "fk_database_metadata_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "semantico",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "column_metadata",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    database_metadata_id = table.Column<int>(type: "integer", nullable: false),
                    column_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    data_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_nullable = table.Column<bool>(type: "boolean", nullable: false),
                    is_primary_key = table.Column<bool>(type: "boolean", nullable: false),
                    is_foreign_key = table.Column<bool>(type: "boolean", nullable: false),
                    ordinal_position = table.Column<int>(type: "integer", nullable: false),
                    foreign_key_table = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    foreign_key_column = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    default_value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    max_length = table.Column<int>(type: "integer", nullable: true),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_column_metadata", x => x.id);
                    table.ForeignKey(
                        name: "fk_column_metadata_database_metadata_database_metadata_id",
                        column: x => x.database_metadata_id,
                        principalSchema: "semantico",
                        principalTable: "database_metadata",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "index_metadata",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    database_metadata_id = table.Column<int>(type: "integer", nullable: false),
                    index_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_unique = table.Column<bool>(type: "boolean", nullable: false),
                    is_primary_key = table.Column<bool>(type: "boolean", nullable: false),
                    columns = table.Column<string[]>(type: "text[]", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_index_metadata", x => x.id);
                    table.ForeignKey(
                        name: "fk_index_metadata_database_metadata_database_metadata_id",
                        column: x => x.database_metadata_id,
                        principalSchema: "semantico",
                        principalTable: "database_metadata",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_column_metadata_database_metadata_id",
                schema: "semantico",
                table: "column_metadata",
                column: "database_metadata_id");

            migrationBuilder.CreateIndex(
                name: "ix_column_metadata_database_metadata_id_column_name",
                schema: "semantico",
                table: "column_metadata",
                columns: new[] { "database_metadata_id", "column_name" });

            migrationBuilder.CreateIndex(
                name: "ix_database_metadata_last_refreshed",
                schema: "semantico",
                table: "database_metadata",
                column: "last_refreshed");

            migrationBuilder.CreateIndex(
                name: "ix_database_metadata_project_id",
                schema: "semantico",
                table: "database_metadata",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_database_metadata_project_id_schema_name_table_name",
                schema: "semantico",
                table: "database_metadata",
                columns: new[] { "project_id", "schema_name", "table_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_index_metadata_database_metadata_id",
                schema: "semantico",
                table: "index_metadata",
                column: "database_metadata_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "column_metadata",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "index_metadata",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "database_metadata",
                schema: "semantico");
        }
    }
}
