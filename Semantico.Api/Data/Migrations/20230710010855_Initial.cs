using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Api.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "semantico");

            migrationBuilder.CreateTable(
                name: "projects",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    connection_string = table.Column<string>(type: "text", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "queries",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    sql_value = table.Column<string>(type: "text", nullable: false),
                    cron_expression = table.Column<string>(type: "text", nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_queries", x => x.id);
                    table.ForeignKey(
                        name: "fk_queries_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "semantico",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    value = table.Column<string>(type: "text", nullable: false),
                    query_id = table.Column<int>(type: "integer", nullable: false),
                    notification_type = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_queries_query_id",
                        column: x => x.query_id,
                        principalSchema: "semantico",
                        principalTable: "queries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_query_id",
                schema: "semantico",
                table: "notifications",
                column: "query_id");

            migrationBuilder.CreateIndex(
                name: "ix_queries_project_id",
                schema: "semantico",
                table: "queries",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "queries",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "projects",
                schema: "semantico");
        }
    }
}
