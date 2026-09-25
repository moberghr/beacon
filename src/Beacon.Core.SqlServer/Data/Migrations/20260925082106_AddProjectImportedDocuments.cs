using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectImportedDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ImportedDocumentId",
                schema: "beacon",
                table: "McpDocChunks",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProjectImportedDocuments",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    SourceKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FrontmatterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ImportedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectImportedDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectImportedDocuments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "beacon",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpDocChunks_ImportedDocumentId",
                schema: "beacon",
                table: "McpDocChunks",
                column: "ImportedDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImportedDocuments_ProjectId_SourceKey_Path",
                schema: "beacon",
                table: "ProjectImportedDocuments",
                columns: new[] { "ProjectId", "SourceKey", "Path" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_McpDocChunks_ProjectImportedDocuments_ImportedDocumentId",
                schema: "beacon",
                table: "McpDocChunks",
                column: "ImportedDocumentId",
                principalSchema: "beacon",
                principalTable: "ProjectImportedDocuments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_McpDocChunks_ProjectImportedDocuments_ImportedDocumentId",
                schema: "beacon",
                table: "McpDocChunks");

            migrationBuilder.DropTable(
                name: "ProjectImportedDocuments",
                schema: "beacon");

            migrationBuilder.DropIndex(
                name: "IX_McpDocChunks_ImportedDocumentId",
                schema: "beacon",
                table: "McpDocChunks");

            migrationBuilder.DropColumn(
                name: "ImportedDocumentId",
                schema: "beacon",
                table: "McpDocChunks");
        }
    }
}
