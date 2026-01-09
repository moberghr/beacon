using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateDocumentationSectionSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "confidence_score",
                schema: "semantico",
                table: "documentation_sections");

            migrationBuilder.RenameColumn(
                name: "column_name",
                schema: "semantico",
                table: "documentation_sections",
                newName: "title");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "title",
                schema: "semantico",
                table: "documentation_sections",
                newName: "column_name");

            migrationBuilder.AddColumn<decimal>(
                name: "confidence_score",
                schema: "semantico",
                table: "documentation_sections",
                type: "numeric",
                nullable: true);
        }
    }
}
