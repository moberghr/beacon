using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuthAndTrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManualQueryExecutionLogs",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    QueryText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResultCount = table.Column<int>(type: "int", nullable: false),
                    ExecutionTimeMs = table.Column<double>(type: "float", nullable: false),
                    DataSourceId = table.Column<int>(type: "int", nullable: true),
                    ExecutionContext = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualQueryExecutionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManualQueryExecutionLogs_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "semantico",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManualQueryExecutionLogs_CreatedTime",
                schema: "semantico",
                table: "ManualQueryExecutionLogs",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_ManualQueryExecutionLogs_DataSourceId",
                schema: "semantico",
                table: "ManualQueryExecutionLogs",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualQueryExecutionLogs_DataSourceId_CreatedTime",
                schema: "semantico",
                table: "ManualQueryExecutionLogs",
                columns: new[] { "DataSourceId", "CreatedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_ManualQueryExecutionLogs_ExecutionContext",
                schema: "semantico",
                table: "ManualQueryExecutionLogs",
                column: "ExecutionContext");

            migrationBuilder.CreateIndex(
                name: "IX_ManualQueryExecutionLogs_UserId",
                schema: "semantico",
                table: "ManualQueryExecutionLogs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualQueryExecutionLogs_UserId_CreatedTime",
                schema: "semantico",
                table: "ManualQueryExecutionLogs",
                columns: new[] { "UserId", "CreatedTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManualQueryExecutionLogs",
                schema: "semantico");
        }
    }
}
