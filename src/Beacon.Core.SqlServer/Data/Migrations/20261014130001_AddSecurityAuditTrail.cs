using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityAuditTrail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive only, no stored value is rewritten and nothing is backfilled: the security audit trail starts empty,
            // existing MCP audit rows keep no content hash and no statements, and existing queries, subscriptions,
            // recipients, data contracts and migration jobs keep no creator, modifier or modification time.
            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                schema: "beacon",
                table: "McpAuditLogs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            AddAttributionColumns(migrationBuilder, "Queries");
            AddAttributionColumns(migrationBuilder, "Subscriptions");
            AddAttributionColumns(migrationBuilder, "Recipients");
            AddAttributionColumns(migrationBuilder, "DataContracts");
            AddAttributionColumns(migrationBuilder, "MigrationJobs");

            // One row per statement an audited MCP call ran, deleted with its audit row (so the audit purge covers it).
            migrationBuilder.CreateTable(
                name: "McpAuditStatements",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    McpAuditLogId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    DataSourceId = table.Column<int>(type: "int", nullable: true),
                    Purpose = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SqlHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Sql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TablesJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RowCount = table.Column<int>(type: "int", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: true),
                    ErrorClass = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpAuditStatements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpAuditStatements_McpAuditLogs_McpAuditLogId",
                        column: x => x.McpAuditLogId,
                        principalSchema: "beacon",
                        principalTable: "McpAuditLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // The security audit trail: append-only, no foreign keys (an event outlives what it names).
            migrationBuilder.CreateTable(
                name: "SecurityAuditEvents",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventGuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ActorKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ActorUserId = table.Column<int>(type: "int", nullable: true),
                    ApiKeyId = table.Column<int>(type: "int", nullable: true),
                    CallerHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    ResourceType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ResourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DetailsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TraceId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    KeyId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Hmac = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpAuditStatements_McpAuditLogId",
                schema: "beacon",
                table: "McpAuditStatements",
                column: "McpAuditLogId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAuditEvents_ActorUserId_CreatedTime",
                schema: "beacon",
                table: "SecurityAuditEvents",
                columns: new[] { "ActorUserId", "CreatedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAuditEvents_CreatedTime",
                schema: "beacon",
                table: "SecurityAuditEvents",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAuditEvents_EventGuid",
                schema: "beacon",
                table: "SecurityAuditEvents",
                column: "EventGuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAuditEvents_ResourceType_ResourceId",
                schema: "beacon",
                table: "SecurityAuditEvents",
                columns: new[] { "ResourceType", "ResourceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpAuditStatements",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "SecurityAuditEvents",
                schema: "beacon");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                schema: "beacon",
                table: "McpAuditLogs");

            DropAttributionColumns(migrationBuilder, "Queries");
            DropAttributionColumns(migrationBuilder, "Subscriptions");
            DropAttributionColumns(migrationBuilder, "Recipients");
            DropAttributionColumns(migrationBuilder, "DataContracts");
            DropAttributionColumns(migrationBuilder, "MigrationJobs");
        }

        // Who created the resource and who changed it last (Users.ExternalId values, as long as an owner id), and when.
        private static void AddAttributionColumns(MigrationBuilder migrationBuilder, string table)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserId",
                schema: "beacon",
                table: table,
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModifiedByUserId",
                schema: "beacon",
                table: table,
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedTime",
                schema: "beacon",
                table: table,
                type: "datetime2",
                nullable: true);
        }

        private static void DropAttributionColumns(MigrationBuilder migrationBuilder, string table)
        {
            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                schema: "beacon",
                table: table);

            migrationBuilder.DropColumn(
                name: "ModifiedByUserId",
                schema: "beacon",
                table: table);

            migrationBuilder.DropColumn(
                name: "ModifiedTime",
                schema: "beacon",
                table: table);
        }
    }
}
