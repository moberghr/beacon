using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActiveVersionId",
                schema: "semantico",
                table: "Queries",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QueryVersions",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QueryId = table.Column<int>(type: "int", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FinalQuery = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StepsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ChangeSource = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ChangeReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueryVersions_Queries_QueryId",
                        column: x => x.QueryId,
                        principalSchema: "semantico",
                        principalTable: "Queries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Queries_ActiveVersionId",
                schema: "semantico",
                table: "Queries",
                column: "ActiveVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryVersions_QueryId_Status",
                schema: "semantico",
                table: "QueryVersions",
                columns: new[] { "QueryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_QueryVersions_QueryId_VersionNumber",
                schema: "semantico",
                table: "QueryVersions",
                columns: new[] { "QueryId", "VersionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Queries_QueryVersions_ActiveVersionId",
                schema: "semantico",
                table: "Queries",
                column: "ActiveVersionId",
                principalSchema: "semantico",
                principalTable: "QueryVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Queries_QueryVersions_ActiveVersionId",
                schema: "semantico",
                table: "Queries");

            migrationBuilder.DropTable(
                name: "QueryVersions",
                schema: "semantico");

            migrationBuilder.DropIndex(
                name: "IX_Queries_ActiveVersionId",
                schema: "semantico",
                table: "Queries");

            migrationBuilder.DropColumn(
                name: "ActiveVersionId",
                schema: "semantico",
                table: "Queries");
        }
    }
}
