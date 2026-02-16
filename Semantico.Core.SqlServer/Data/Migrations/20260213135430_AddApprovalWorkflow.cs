using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QueryApprovalRequests",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QueryId = table.Column<int>(type: "int", nullable: false),
                    QueryVersionId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RequestedByUserName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReviewedByUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReviewedByUserName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewComment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ChangeSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryApprovalRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueryApprovalRequests_Queries_QueryId",
                        column: x => x.QueryId,
                        principalSchema: "semantico",
                        principalTable: "Queries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QueryApprovalRequests_QueryVersions_QueryVersionId",
                        column: x => x.QueryVersionId,
                        principalSchema: "semantico",
                        principalTable: "QueryVersions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_QueryApprovalRequests_QueryId_Status",
                schema: "semantico",
                table: "QueryApprovalRequests",
                columns: new[] { "QueryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_QueryApprovalRequests_QueryVersionId",
                schema: "semantico",
                table: "QueryApprovalRequests",
                column: "QueryVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryApprovalRequests_Status",
                schema: "semantico",
                table: "QueryApprovalRequests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_QueryApprovalRequests_Status_CreatedTime",
                schema: "semantico",
                table: "QueryApprovalRequests",
                columns: new[] { "Status", "CreatedTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QueryApprovalRequests",
                schema: "semantico");
        }
    }
}
