using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentationSectionEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "edited_by_user_id",
                schema: "beacon",
                table: "project_documentation_sections",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "edited_time",
                schema: "beacon",
                table: "project_documentation_sections",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "edited_by_user_id",
                schema: "beacon",
                table: "project_documentation_sections");

            migrationBuilder.DropColumn(
                name: "edited_time",
                schema: "beacon",
                table: "project_documentation_sections");
        }
    }
}
