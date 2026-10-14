using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChangeApprovalRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive, no data rewrite: existing migration jobs, Custom SQL rules and recipients keep no approved hash,
            // which the change approval workflow reads as not governed — they work as before.
            migrationBuilder.AddColumn<string>(
                name: "ApprovedDefinitionHash",
                schema: "beacon",
                table: "MigrationJobs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedSqlHash",
                schema: "beacon",
                table: "DataContractRules",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedDestinationHash",
                schema: "beacon",
                table: "Recipients",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            // Existing evaluations left out no rule waiting for approval, and their alerts held no recipient.
            migrationBuilder.AddColumn<int>(
                name: "AwaitingApprovalRules",
                schema: "beacon",
                table: "DataQualityEvaluations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "HeldAlertRecipients",
                schema: "beacon",
                table: "DataQualityEvaluations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ChangeApprovalRequests",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ResourceId = table.Column<int>(type: "int", nullable: false),
                    ResourceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DefinitionHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProposedJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PreviousJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                    table.PrimaryKey("PK_ChangeApprovalRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeApprovalRequests_Kind_ResourceId_Status",
                schema: "beacon",
                table: "ChangeApprovalRequests",
                columns: new[] { "Kind", "ResourceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeApprovalRequests_Status_CreatedTime",
                schema: "beacon",
                table: "ChangeApprovalRequests",
                columns: new[] { "Status", "CreatedTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChangeApprovalRequests",
                schema: "beacon");

            migrationBuilder.DropColumn(
                name: "ApprovedDefinitionHash",
                schema: "beacon",
                table: "MigrationJobs");

            migrationBuilder.DropColumn(
                name: "ApprovedSqlHash",
                schema: "beacon",
                table: "DataContractRules");

            migrationBuilder.DropColumn(
                name: "ApprovedDestinationHash",
                schema: "beacon",
                table: "Recipients");

            migrationBuilder.DropColumn(
                name: "AwaitingApprovalRules",
                schema: "beacon",
                table: "DataQualityEvaluations");

            migrationBuilder.DropColumn(
                name: "HeldAlertRecipients",
                schema: "beacon",
                table: "DataQualityEvaluations");
        }
    }
}
