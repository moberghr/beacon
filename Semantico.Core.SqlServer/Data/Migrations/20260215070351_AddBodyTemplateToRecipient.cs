using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBodyTemplateToRecipient : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BodyTemplate",
                schema: "semantico",
                table: "Recipients",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BodyTemplate",
                schema: "semantico",
                table: "Recipients");
        }
    }
}
