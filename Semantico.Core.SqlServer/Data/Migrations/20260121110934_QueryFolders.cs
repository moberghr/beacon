using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class QueryFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FolderId",
                schema: "semantico",
                table: "Queries",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QueryFolders",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ParentFolderId = table.Column<int>(type: "int", nullable: true),
                    Path = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueryFolders_QueryFolders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalSchema: "semantico",
                        principalTable: "QueryFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Queries_FolderId",
                schema: "semantico",
                table: "Queries",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryFolders_ArchivedTime",
                schema: "semantico",
                table: "QueryFolders",
                column: "ArchivedTime");

            migrationBuilder.CreateIndex(
                name: "IX_QueryFolders_ParentFolderId",
                schema: "semantico",
                table: "QueryFolders",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryFolders_ParentFolderId_Name",
                schema: "semantico",
                table: "QueryFolders",
                columns: new[] { "ParentFolderId", "Name" },
                unique: true,
                filter: "[ParentFolderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_QueryFolders_ParentFolderId_SortOrder",
                schema: "semantico",
                table: "QueryFolders",
                columns: new[] { "ParentFolderId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_QueryFolders_Path",
                schema: "semantico",
                table: "QueryFolders",
                column: "Path");

            migrationBuilder.AddForeignKey(
                name: "FK_Queries_QueryFolders_FolderId",
                schema: "semantico",
                table: "Queries",
                column: "FolderId",
                principalSchema: "semantico",
                principalTable: "QueryFolders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Queries_QueryFolders_FolderId",
                schema: "semantico",
                table: "Queries");

            migrationBuilder.DropTable(
                name: "QueryFolders",
                schema: "semantico");

            migrationBuilder.DropIndex(
                name: "IX_Queries_FolderId",
                schema: "semantico",
                table: "Queries");

            migrationBuilder.DropColumn(
                name: "FolderId",
                schema: "semantico",
                table: "Queries");
        }
    }
}
