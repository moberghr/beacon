using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserManagementCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_queries_query_versions_active_version_id",
                schema: "semantico",
                table: "queries");

            migrationBuilder.AddForeignKey(
                name: "fk_queries_query_versions_active_version_id",
                schema: "semantico",
                table: "queries",
                column: "active_version_id",
                principalSchema: "semantico",
                principalTable: "query_versions",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_queries_query_versions_active_version_id",
                schema: "semantico",
                table: "queries");

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
    }
}
