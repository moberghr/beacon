using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
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
                name: "approved_definition_hash",
                schema: "beacon",
                table: "migration_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "approved_sql_hash",
                schema: "beacon",
                table: "data_contract_rules",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "approved_destination_hash",
                schema: "beacon",
                table: "recipients",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Existing evaluations left out no rule waiting for approval, and their alerts held no recipient.
            migrationBuilder.AddColumn<int>(
                name: "awaiting_approval_rules",
                schema: "beacon",
                table: "data_quality_evaluations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "held_alert_recipients",
                schema: "beacon",
                table: "data_quality_evaluations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "change_approval_requests",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    resource_id = table.Column<int>(type: "integer", nullable: false),
                    resource_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    definition_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    proposed_json = table.Column<string>(type: "text", nullable: false),
                    previous_json = table.Column<string>(type: "text", nullable: true),
                    requested_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    requested_by_user_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reviewed_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reviewed_by_user_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    review_comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    change_summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_approval_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_approval_requests_kind_resource_id_status",
                schema: "beacon",
                table: "change_approval_requests",
                columns: new[] { "kind", "resource_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_change_approval_requests_status_created_time",
                schema: "beacon",
                table: "change_approval_requests",
                columns: new[] { "status", "created_time" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_approval_requests",
                schema: "beacon");

            migrationBuilder.DropColumn(
                name: "approved_definition_hash",
                schema: "beacon",
                table: "migration_jobs");

            migrationBuilder.DropColumn(
                name: "approved_sql_hash",
                schema: "beacon",
                table: "data_contract_rules");

            migrationBuilder.DropColumn(
                name: "approved_destination_hash",
                schema: "beacon",
                table: "recipients");

            migrationBuilder.DropColumn(
                name: "awaiting_approval_rules",
                schema: "beacon",
                table: "data_quality_evaluations");

            migrationBuilder.DropColumn(
                name: "held_alert_recipients",
                schema: "beacon",
                table: "data_quality_evaluations");
        }
    }
}
