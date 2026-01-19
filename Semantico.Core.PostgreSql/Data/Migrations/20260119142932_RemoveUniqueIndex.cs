using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_query_tasks_subscription_id",
                schema: "semantico",
                table: "query_tasks");

            migrationBuilder.CreateIndex(
                name: "ix_query_tasks_subscription_id",
                schema: "semantico",
                table: "query_tasks",
                column: "subscription_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_query_tasks_subscription_id",
                schema: "semantico",
                table: "query_tasks");

            migrationBuilder.CreateIndex(
                name: "ix_query_tasks_subscription_id",
                schema: "semantico",
                table: "query_tasks",
                column: "subscription_id",
                unique: true);
        }
    }
}
