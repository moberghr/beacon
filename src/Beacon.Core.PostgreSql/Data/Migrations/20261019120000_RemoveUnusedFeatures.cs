using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUnusedFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Removes the tables of features that no code reads or writes any more (dashboards, MCP sessions, AI alert
            // configurations and their conversation history, AI usage metrics) and the audit log's key to MCP sessions, which was never
            // set. The live Mcp-Session-Id header column (mcp_session_id) stays.
            migrationBuilder.DropForeignKey(
                name: "fk_mcp_audit_logs_mcp_sessions_session_id",
                schema: "beacon",
                table: "mcp_audit_logs");

            migrationBuilder.DropIndex(
                name: "ix_mcp_audit_logs_session_id",
                schema: "beacon",
                table: "mcp_audit_logs");

            migrationBuilder.DropColumn(
                name: "session_id",
                schema: "beacon",
                table: "mcp_audit_logs");

            migrationBuilder.DropTable(
                name: "ai_conversation_histories",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "ai_alert_configurations",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "dashboard_permissions",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "dashboard_widgets",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "dashboards",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "mcp_sessions",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "ai_usage_metrics",
                schema: "beacon");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_usage_metrics",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    data_source_id = table.Column<int>(type: "integer", nullable: true),
                    query_id = table.Column<int>(type: "integer", nullable: true),
                    documentation_id = table.Column<int>(type: "integer", nullable: true),
                    provider = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    operation_type = table.Column<int>(type: "integer", nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    total_tokens = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric", nullable: false),
                    prompt_cache_hit = table.Column<bool>(type: "boolean", nullable: false),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_usage_metrics", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_usage_metrics_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalSchema: "beacon",
                        principalTable: "data_sources",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_ai_usage_metrics_queries_query_id",
                        column: x => x.query_id,
                        principalSchema: "beacon",
                        principalTable: "queries",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_data_source_id",
                schema: "beacon",
                table: "ai_usage_metrics",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_operation_type",
                schema: "beacon",
                table: "ai_usage_metrics",
                column: "operation_type");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_provider",
                schema: "beacon",
                table: "ai_usage_metrics",
                column: "provider");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_query_id",
                schema: "beacon",
                table: "ai_usage_metrics",
                column: "query_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_timestamp",
                schema: "beacon",
                table: "ai_usage_metrics",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_user_id",
                schema: "beacon",
                table: "ai_usage_metrics",
                column: "user_id");

            migrationBuilder.CreateTable(
                name: "mcp_sessions",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    api_key_id = table.Column<int>(type: "integer", nullable: true),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    session_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_activity_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    queries_executed = table.Column<int>(type: "integer", nullable: false),
                    tokens_used = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_mcp_sessions_api_key_credentials_api_key_id",
                        column: x => x.api_key_id,
                        principalSchema: "beacon",
                        principalTable: "api_key_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_mcp_sessions_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "beacon",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "dashboards",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_by_user_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_shared = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    refresh_interval_seconds = table.Column<int>(type: "integer", nullable: true),
                    layout_configuration = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dashboards", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dashboard_permissions",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    dashboard_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    permission_level = table.Column<int>(type: "integer", nullable: false),
                    granted_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    granted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dashboard_permissions", x => x.id);
                    table.ForeignKey(
                        name: "fk_dashboard_permissions_dashboards_dashboard_id",
                        column: x => x.dashboard_id,
                        principalSchema: "beacon",
                        principalTable: "dashboards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dashboard_widgets",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    dashboard_id = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    widget_type = table.Column<int>(type: "integer", nullable: false),
                    configuration_json = table.Column<string>(type: "text", nullable: false),
                    position_x = table.Column<int>(type: "integer", nullable: false),
                    position_y = table.Column<int>(type: "integer", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    refresh_interval_seconds = table.Column<int>(type: "integer", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dashboard_widgets", x => x.id);
                    table.ForeignKey(
                        name: "fk_dashboard_widgets_dashboards_dashboard_id",
                        column: x => x.dashboard_id,
                        principalSchema: "beacon",
                        principalTable: "dashboards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_alert_configurations",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    natural_language_description = table.Column<string>(type: "text", nullable: false),
                    generated_sql = table.Column<string>(type: "text", nullable: false),
                    final_sql = table.Column<string>(type: "text", nullable: true),
                    generated_by_model = table.Column<string>(type: "text", nullable: false),
                    generation_reasoning = table.Column<string>(type: "text", nullable: true),
                    confidence_score = table.Column<decimal>(type: "numeric", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    validation_errors = table.Column<string>(type: "text", nullable: true),
                    user_feedback = table.Column<string>(type: "text", nullable: true),
                    subscription_id = table.Column<int>(type: "integer", nullable: true),
                    conversation_turns = table.Column<int>(type: "integer", nullable: false),
                    tokens_used = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    modified_by = table.Column<string>(type: "text", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_alert_configurations", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_alert_configurations_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalSchema: "beacon",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ai_alert_configurations_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "beacon",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ai_conversation_histories",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ai_alert_configuration_id = table.Column<int>(type: "integer", nullable: false),
                    turn_number = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    message_content = table.Column<string>(type: "text", nullable: false),
                    tokens_used = table.Column<int>(type: "integer", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    metadata = table.Column<string>(type: "text", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_conversation_histories", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_conversation_histories_ai_alert_configurations_ai_alert_",
                        column: x => x.ai_alert_configuration_id,
                        principalSchema: "beacon",
                        principalTable: "ai_alert_configurations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddColumn<int>(
                name: "session_id",
                schema: "beacon",
                table: "mcp_audit_logs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_mcp_sessions_api_key_id",
                schema: "beacon",
                table: "mcp_sessions",
                column: "api_key_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_sessions_last_activity_at",
                schema: "beacon",
                table: "mcp_sessions",
                column: "last_activity_at");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_sessions_session_id",
                schema: "beacon",
                table: "mcp_sessions",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mcp_sessions_user_id",
                schema: "beacon",
                table: "mcp_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_dashboards_archived_time",
                schema: "beacon",
                table: "dashboards",
                column: "archived_time");

            migrationBuilder.CreateIndex(
                name: "ix_dashboards_created_by_user_id",
                schema: "beacon",
                table: "dashboards",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_dashboards_is_default",
                schema: "beacon",
                table: "dashboards",
                column: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_dashboards_is_shared",
                schema: "beacon",
                table: "dashboards",
                column: "is_shared");

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_permissions_dashboard_id",
                schema: "beacon",
                table: "dashboard_permissions",
                column: "dashboard_id");

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_permissions_dashboard_id_user_id",
                schema: "beacon",
                table: "dashboard_permissions",
                columns: new[] { "dashboard_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_permissions_user_id",
                schema: "beacon",
                table: "dashboard_permissions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_widgets_dashboard_id",
                schema: "beacon",
                table: "dashboard_widgets",
                column: "dashboard_id");

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_widgets_dashboard_id_sort_order",
                schema: "beacon",
                table: "dashboard_widgets",
                columns: new[] { "dashboard_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_widgets_widget_type",
                schema: "beacon",
                table: "dashboard_widgets",
                column: "widget_type");

            migrationBuilder.CreateIndex(
                name: "ix_ai_alert_configurations_data_source_id",
                schema: "beacon",
                table: "ai_alert_configurations",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_alert_configurations_status",
                schema: "beacon",
                table: "ai_alert_configurations",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_ai_alert_configurations_subscription_id",
                schema: "beacon",
                table: "ai_alert_configurations",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_conversation_histories_ai_alert_configuration_id",
                schema: "beacon",
                table: "ai_conversation_histories",
                column: "ai_alert_configuration_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_conversation_histories_timestamp",
                schema: "beacon",
                table: "ai_conversation_histories",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_ai_conversation_histories_turn_number",
                schema: "beacon",
                table: "ai_conversation_histories",
                column: "turn_number");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_audit_logs_session_id",
                schema: "beacon",
                table: "mcp_audit_logs",
                column: "session_id");

            migrationBuilder.AddForeignKey(
                name: "fk_mcp_audit_logs_mcp_sessions_session_id",
                schema: "beacon",
                table: "mcp_audit_logs",
                column: "session_id",
                principalSchema: "beacon",
                principalTable: "mcp_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
