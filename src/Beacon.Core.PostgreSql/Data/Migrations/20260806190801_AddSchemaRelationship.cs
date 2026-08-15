using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSchemaRelationship : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "schema_relationships",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    source_schema = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    source_table = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_column = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    target_schema = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    target_table = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    target_column = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    origin = table.Column<int>(type: "integer", nullable: false),
                    cardinality = table.Column<int>(type: "integer", nullable: false),
                    constraint_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    is_verified = table.Column<bool>(type: "boolean", nullable: false),
                    verified_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    verified_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schema_relationships", x => x.id);
                    table.ForeignKey(
                        name: "fk_schema_relationships_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_schema_relationships_data_source_id_origin",
                table: "schema_relationships",
                columns: new[] { "data_source_id", "origin" });

            migrationBuilder.CreateIndex(
                name: "ix_schema_relationships_data_source_id_source_schema_source_ta",
                table: "schema_relationships",
                columns: new[] { "data_source_id", "source_schema", "source_table", "source_column", "target_schema", "target_table", "target_column" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "schema_relationships");
        }
    }
}
