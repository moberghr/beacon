using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "active_version_id",
                schema: "semantico",
                table: "queries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "query_versions",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    query_id = table.Column<int>(type: "integer", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    final_query = table.Column<string>(type: "text", nullable: true),
                    steps_json = table.Column<string>(type: "text", nullable: false),
                    created_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    change_source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    change_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_query_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_query_versions_queries_query_id",
                        column: x => x.query_id,
                        principalSchema: "semantico",
                        principalTable: "queries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_queries_active_version_id",
                schema: "semantico",
                table: "queries",
                column: "active_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_versions_query_id_status",
                schema: "semantico",
                table: "query_versions",
                columns: new[] { "query_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_query_versions_query_id_version_number",
                schema: "semantico",
                table: "query_versions",
                columns: new[] { "query_id", "version_number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_queries_query_versions_active_version_id",
                schema: "semantico",
                table: "queries",
                column: "active_version_id",
                principalSchema: "semantico",
                principalTable: "query_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_queries_query_versions_active_version_id",
                schema: "semantico",
                table: "queries");

            migrationBuilder.DropTable(
                name: "query_versions",
                schema: "semantico");

            migrationBuilder.DropIndex(
                name: "ix_queries_active_version_id",
                schema: "semantico",
                table: "queries");

            migrationBuilder.DropColumn(
                name: "active_version_id",
                schema: "semantico",
                table: "queries");
        }
    }
}
