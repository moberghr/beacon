using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
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
                name: "content_hash",
                schema: "beacon",
                table: "mcp_audit_logs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            AddAttributionColumns(migrationBuilder, "queries");
            AddAttributionColumns(migrationBuilder, "subscriptions");
            AddAttributionColumns(migrationBuilder, "recipients");
            AddAttributionColumns(migrationBuilder, "data_contracts");
            AddAttributionColumns(migrationBuilder, "migration_jobs");

            // One row per statement an audited MCP call ran, deleted with its audit row (so the audit purge covers it).
            migrationBuilder.CreateTable(
                name: "mcp_audit_statements",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    mcp_audit_log_id = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: true),
                    data_source_id = table.Column<int>(type: "integer", nullable: true),
                    purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sql_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sql = table.Column<string>(type: "text", nullable: true),
                    tables_json = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    row_count = table.Column<int>(type: "integer", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    error_class = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_audit_statements", x => x.id);
                    table.ForeignKey(
                        name: "fk_mcp_audit_statements_mcp_audit_logs_mcp_audit_log_id",
                        column: x => x.mcp_audit_log_id,
                        principalSchema: "beacon",
                        principalTable: "mcp_audit_logs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // The security audit trail: append-only, no foreign keys (an event outlives what it names).
            migrationBuilder.CreateTable(
                name: "security_audit_events",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_user_id = table.Column<int>(type: "integer", nullable: true),
                    api_key_id = table.Column<int>(type: "integer", nullable: true),
                    caller_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    project_id = table.Column<int>(type: "integer", nullable: true),
                    resource_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    resource_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    details_json = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    trace_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    key_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    hmac = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_audit_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_audit_statements_mcp_audit_log_id",
                schema: "beacon",
                table: "mcp_audit_statements",
                column: "mcp_audit_log_id");

            migrationBuilder.CreateIndex(
                name: "ix_security_audit_events_actor_user_id_created_time",
                schema: "beacon",
                table: "security_audit_events",
                columns: new[] { "actor_user_id", "created_time" });

            migrationBuilder.CreateIndex(
                name: "ix_security_audit_events_created_time",
                schema: "beacon",
                table: "security_audit_events",
                column: "created_time");

            migrationBuilder.CreateIndex(
                name: "ix_security_audit_events_event_guid",
                schema: "beacon",
                table: "security_audit_events",
                column: "event_guid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_security_audit_events_resource_type_resource_id",
                schema: "beacon",
                table: "security_audit_events",
                columns: new[] { "resource_type", "resource_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_audit_statements",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "security_audit_events",
                schema: "beacon");

            migrationBuilder.DropColumn(
                name: "content_hash",
                schema: "beacon",
                table: "mcp_audit_logs");

            DropAttributionColumns(migrationBuilder, "queries");
            DropAttributionColumns(migrationBuilder, "subscriptions");
            DropAttributionColumns(migrationBuilder, "recipients");
            DropAttributionColumns(migrationBuilder, "data_contracts");
            DropAttributionColumns(migrationBuilder, "migration_jobs");
        }

        // Who created the resource and who changed it last (Users.ExternalId values, as long as an owner id), and when.
        private static void AddAttributionColumns(MigrationBuilder migrationBuilder, string table)
        {
            migrationBuilder.AddColumn<string>(
                name: "created_by_user_id",
                schema: "beacon",
                table: table,
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "modified_by_user_id",
                schema: "beacon",
                table: table,
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "modified_time",
                schema: "beacon",
                table: table,
                type: "timestamp with time zone",
                nullable: true);
        }

        private static void DropAttributionColumns(MigrationBuilder migrationBuilder, string table)
        {
            migrationBuilder.DropColumn(
                name: "created_by_user_id",
                schema: "beacon",
                table: table);

            migrationBuilder.DropColumn(
                name: "modified_by_user_id",
                schema: "beacon",
                table: table);

            migrationBuilder.DropColumn(
                name: "modified_time",
                schema: "beacon",
                table: table);
        }
    }
}
