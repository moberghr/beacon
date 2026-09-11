using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpProjectSettingsAndLocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allow_explicit_feedback_content",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "max_concurrent_queries_per_key",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 4);

            migrationBuilder.AddColumn<decimal>(
                name: "max_explain_cost",
                table: "mcp_settings",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_result_bytes",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 262144);

            migrationBuilder.AddColumn<bool>(
                name: "retain_query_content",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "statement_timeout_seconds",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<string>(
                name: "caller_hash",
                table: "mcp_query_signals",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_read_only",
                table: "data_sources",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "use_read_only_intent",
                table: "data_sources",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "mcp_project_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    max_row_limit = table.Column<int>(type: "integer", nullable: true),
                    enforce_read_only = table.Column<bool>(type: "boolean", nullable: true),
                    enable_pii_detection = table.Column<bool>(type: "boolean", nullable: true),
                    custom_pii_patterns = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    enable_sample_value_collection = table.Column<bool>(type: "boolean", nullable: true),
                    enable_learning = table.Column<bool>(type: "boolean", nullable: true),
                    learning_auto_approve_threshold = table.Column<double>(type: "double precision", nullable: true),
                    learning_injection_budget_chars = table.Column<int>(type: "integer", nullable: true),
                    learning_signal_retention_days = table.Column<int>(type: "integer", nullable: true),
                    enable_self_consistency = table.Column<bool>(type: "boolean", nullable: true),
                    self_consistency_candidate_count = table.Column<int>(type: "integer", nullable: true),
                    enable_eval_judge = table.Column<bool>(type: "boolean", nullable: true),
                    enable_semantic_retrieval = table.Column<bool>(type: "boolean", nullable: true),
                    exemplar_top_k = table.Column<int>(type: "integer", nullable: true),
                    enable_replay_verification = table.Column<bool>(type: "boolean", nullable: true),
                    learning_replay_min_flips = table.Column<int>(type: "integer", nullable: true),
                    enable_contextual_retrieval = table.Column<bool>(type: "boolean", nullable: true),
                    doc_chunk_window_sentences = table.Column<int>(type: "integer", nullable: true),
                    doc_chunk_overlap_sentences = table.Column<int>(type: "integer", nullable: true),
                    glossary_top_k = table.Column<int>(type: "integer", nullable: true),
                    doc_chunk_top_k = table.Column<int>(type: "integer", nullable: true),
                    enable_golden_exemplars = table.Column<bool>(type: "boolean", nullable: true),
                    golden_exemplar_top_k = table.Column<int>(type: "integer", nullable: true),
                    golden_exemplar_budget_chars = table.Column<int>(type: "integer", nullable: true),
                    enable_value_grounding = table.Column<bool>(type: "boolean", nullable: true),
                    value_grounding_max_probes = table.Column<int>(type: "integer", nullable: true),
                    enable_semantic_lint = table.Column<bool>(type: "boolean", nullable: true),
                    self_consistency_min_tables = table.Column<int>(type: "integer", nullable: true),
                    retain_query_content = table.Column<bool>(type: "boolean", nullable: true),
                    statement_timeout_seconds = table.Column<int>(type: "integer", nullable: true),
                    max_result_bytes = table.Column<int>(type: "integer", nullable: true),
                    max_explain_cost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    max_concurrent_queries_per_key = table.Column<int>(type: "integer", nullable: true),
                    allow_explicit_feedback_content = table.Column<bool>(type: "boolean", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_project_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_mcp_project_settings_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_project_settings_project_id",
                table: "mcp_project_settings",
                column: "project_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_project_settings");

            migrationBuilder.DropColumn(
                name: "allow_explicit_feedback_content",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "max_concurrent_queries_per_key",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "max_explain_cost",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "max_result_bytes",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "retain_query_content",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "statement_timeout_seconds",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "caller_hash",
                table: "mcp_query_signals");

            migrationBuilder.DropColumn(
                name: "is_read_only",
                table: "data_sources");

            migrationBuilder.DropColumn(
                name: "use_read_only_intent",
                table: "data_sources");
        }
    }
}
