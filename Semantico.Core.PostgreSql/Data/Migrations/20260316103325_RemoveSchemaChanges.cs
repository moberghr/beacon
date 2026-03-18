using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSchemaChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "schema_changes",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "schema_snapshots",
                schema: "semantico");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "schema_changes",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    change_type = table.Column<int>(type: "integer", nullable: false),
                    column_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    detected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    new_value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    old_value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    schema_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    table_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schema_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_schema_changes_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "schema_snapshots",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    captured_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    schema_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schema_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "fk_schema_snapshots_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_schema_changes_change_type",
                schema: "semantico",
                table: "schema_changes",
                column: "change_type");

            migrationBuilder.CreateIndex(
                name: "ix_schema_changes_data_source_id",
                schema: "semantico",
                table: "schema_changes",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_schema_changes_detected_at",
                schema: "semantico",
                table: "schema_changes",
                column: "detected_at");

            migrationBuilder.CreateIndex(
                name: "ix_schema_snapshots_captured_at",
                schema: "semantico",
                table: "schema_snapshots",
                column: "captured_at");

            migrationBuilder.CreateIndex(
                name: "ix_schema_snapshots_data_source_id",
                schema: "semantico",
                table: "schema_snapshots",
                column: "data_source_id");
        }
    }
}
