using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFinalQueryToQuery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "final_query",
                schema: "semantico",
                table: "queries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "final_query_project_id",
                schema: "semantico",
                table: "queries",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "final_query",
                schema: "semantico",
                table: "queries");

            migrationBuilder.DropColumn(
                name: "final_query_project_id",
                schema: "semantico",
                table: "queries");
        }
    }
}
