using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentationSectionEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EditedByUserId",
                schema: "beacon",
                table: "ProjectDocumentationSections",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EditedTime",
                schema: "beacon",
                table: "ProjectDocumentationSections",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EditedByUserId",
                schema: "beacon",
                table: "ProjectDocumentationSections");

            migrationBuilder.DropColumn(
                name: "EditedTime",
                schema: "beacon",
                table: "ProjectDocumentationSections");
        }
    }
}
