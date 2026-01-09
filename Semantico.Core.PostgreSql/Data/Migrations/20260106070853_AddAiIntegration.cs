using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_alert_configurations",
                schema: "semantico",
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
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    modified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
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
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ai_alert_configurations_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "semantico",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ai_prompt_templates",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    operation_type = table.Column<int>(type: "integer", nullable: false),
                    prompt_template = table.Column<string>(type: "text", nullable: false),
                    system_prompt = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    temperature = table.Column<decimal>(type: "numeric", nullable: false),
                    max_tokens = table.Column<int>(type: "integer", nullable: false),
                    variable_definitions = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    modified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    modified_by = table.Column<string>(type: "text", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_prompt_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_usage_metrics",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    data_source_id = table.Column<int>(type: "integer", nullable: true),
                    query_id = table.Column<int>(type: "integer", nullable: true),
                    documentation_id = table.Column<int>(type: "integer", nullable: true),
                    alert_config_id = table.Column<int>(type: "integer", nullable: true),
                    provider = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    operation_type = table.Column<int>(type: "integer", nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    total_tokens = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric", nullable: false),
                    prompt_cache_hit = table.Column<bool>(type: "boolean", nullable: false),
                    response_time_ms = table.Column<int>(type: "integer", nullable: false),
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
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_ai_usage_metrics_queries_query_id",
                        column: x => x.query_id,
                        principalSchema: "semantico",
                        principalTable: "queries",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "data_source_documentations",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    generated_by_model = table.Column<string>(type: "text", nullable: false),
                    generated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    generated_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    last_modified_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    last_modified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    tables_analyzed = table.Column<int>(type: "integer", nullable: false),
                    tokens_used = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric", nullable: false),
                    metadata = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    modified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    modified_by = table.Column<string>(type: "text", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_source_documentations", x => x.id);
                    table.ForeignKey(
                        name: "fk_data_source_documentations_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_conversation_histories",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "ai_alert_configurations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "documentation_sections",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    documentation_id = table.Column<int>(type: "integer", nullable: false),
                    section_type = table.Column<int>(type: "integer", nullable: false),
                    table_name = table.Column<string>(type: "text", nullable: true),
                    column_name = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    ai_generated_content = table.Column<string>(type: "text", nullable: false),
                    user_edited_content = table.Column<string>(type: "text", nullable: true),
                    is_user_edited = table.Column<bool>(type: "boolean", nullable: false),
                    content_format = table.Column<int>(type: "integer", nullable: false),
                    confidence_score = table.Column<decimal>(type: "numeric", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    modified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    modified_by = table.Column<string>(type: "text", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documentation_sections", x => x.id);
                    table.ForeignKey(
                        name: "fk_documentation_sections_data_source_documentations_documenta",
                        column: x => x.documentation_id,
                        principalSchema: "semantico",
                        principalTable: "data_source_documentations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "documentation_versions",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    documentation_id = table.Column<int>(type: "integer", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    change_description = table.Column<string>(type: "text", nullable: true),
                    snapshot_json = table.Column<string>(type: "text", nullable: false),
                    sections_count = table.Column<int>(type: "integer", nullable: false),
                    tokens_used = table.Column<int>(type: "integer", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documentation_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_documentation_versions_data_source_documentations_documenta",
                        column: x => x.documentation_id,
                        principalSchema: "semantico",
                        principalTable: "data_source_documentations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_alert_configurations_data_source_id",
                schema: "semantico",
                table: "ai_alert_configurations",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_alert_configurations_status",
                schema: "semantico",
                table: "ai_alert_configurations",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_ai_alert_configurations_subscription_id",
                schema: "semantico",
                table: "ai_alert_configurations",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_conversation_histories_ai_alert_configuration_id",
                schema: "semantico",
                table: "ai_conversation_histories",
                column: "ai_alert_configuration_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_conversation_histories_timestamp",
                schema: "semantico",
                table: "ai_conversation_histories",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_ai_conversation_histories_turn_number",
                schema: "semantico",
                table: "ai_conversation_histories",
                column: "turn_number");

            migrationBuilder.CreateIndex(
                name: "ix_ai_prompt_templates_is_active",
                schema: "semantico",
                table: "ai_prompt_templates",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_ai_prompt_templates_operation_type",
                schema: "semantico",
                table: "ai_prompt_templates",
                column: "operation_type");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_data_source_id",
                schema: "semantico",
                table: "ai_usage_metrics",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_operation_type",
                schema: "semantico",
                table: "ai_usage_metrics",
                column: "operation_type");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_provider",
                schema: "semantico",
                table: "ai_usage_metrics",
                column: "provider");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_query_id",
                schema: "semantico",
                table: "ai_usage_metrics",
                column: "query_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_timestamp",
                schema: "semantico",
                table: "ai_usage_metrics",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_user_id",
                schema: "semantico",
                table: "ai_usage_metrics",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_source_documentations_data_source_id",
                schema: "semantico",
                table: "data_source_documentations",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_source_documentations_generated_at",
                schema: "semantico",
                table: "data_source_documentations",
                column: "generated_at");

            migrationBuilder.CreateIndex(
                name: "ix_data_source_documentations_status",
                schema: "semantico",
                table: "data_source_documentations",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_documentation_sections_documentation_id",
                schema: "semantico",
                table: "documentation_sections",
                column: "documentation_id");

            migrationBuilder.CreateIndex(
                name: "ix_documentation_sections_section_type",
                schema: "semantico",
                table: "documentation_sections",
                column: "section_type");

            migrationBuilder.CreateIndex(
                name: "ix_documentation_sections_table_name",
                schema: "semantico",
                table: "documentation_sections",
                column: "table_name");

            migrationBuilder.CreateIndex(
                name: "ix_documentation_versions_created_at",
                schema: "semantico",
                table: "documentation_versions",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_documentation_versions_documentation_id",
                schema: "semantico",
                table: "documentation_versions",
                column: "documentation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_conversation_histories",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "ai_prompt_templates",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "ai_usage_metrics",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "documentation_sections",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "documentation_versions",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "ai_alert_configurations",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "data_source_documentations",
                schema: "semantico");
        }
    }
}
