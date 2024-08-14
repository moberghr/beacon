using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.Migrations
{
    /// <inheritdoc />
    public partial class ProjectAddDatabaseEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subscription_parameters_subscription_id_query_placeholder",
                schema: "semantico",
                table: "subscription_parameters");

            migrationBuilder.DropIndex(
                name: "ix_query_parameters_query_id_placeholder",
                schema: "semantico",
                table: "query_parameters");

            migrationBuilder.AddColumn<int>(
                name: "database_engine_type",
                schema: "semantico",
                table: "projects",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_subscription_parameters_subscription_id",
                schema: "semantico",
                table: "subscription_parameters",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_parameters_query_id",
                schema: "semantico",
                table: "query_parameters",
                column: "query_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subscription_parameters_subscription_id",
                schema: "semantico",
                table: "subscription_parameters");

            migrationBuilder.DropIndex(
                name: "ix_query_parameters_query_id",
                schema: "semantico",
                table: "query_parameters");

            migrationBuilder.DropColumn(
                name: "database_engine_type",
                schema: "semantico",
                table: "projects");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_parameters_subscription_id_query_placeholder",
                schema: "semantico",
                table: "subscription_parameters",
                columns: new[] { "subscription_id", "query_placeholder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_query_parameters_query_id_placeholder",
                schema: "semantico",
                table: "query_parameters",
                columns: new[] { "query_id", "placeholder" },
                unique: true);
        }
    }
}
