using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "query_approval_requests",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    query_id = table.Column<int>(type: "integer", nullable: false),
                    query_version_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    requested_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    requested_by_user_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reviewed_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reviewed_by_user_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    review_comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    change_summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_query_approval_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_query_approval_requests_queries_query_id",
                        column: x => x.query_id,
                        principalSchema: "semantico",
                        principalTable: "queries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_query_approval_requests_query_versions_query_version_id",
                        column: x => x.query_version_id,
                        principalSchema: "semantico",
                        principalTable: "query_versions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_query_approval_requests_query_id_status",
                schema: "semantico",
                table: "query_approval_requests",
                columns: new[] { "query_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_query_approval_requests_query_version_id",
                schema: "semantico",
                table: "query_approval_requests",
                column: "query_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_approval_requests_status",
                schema: "semantico",
                table: "query_approval_requests",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_query_approval_requests_status_created_time",
                schema: "semantico",
                table: "query_approval_requests",
                columns: new[] { "status", "created_time" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "query_approval_requests",
                schema: "semantico");
        }
    }
}
