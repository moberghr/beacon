using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpEval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mcp_eval_cases",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    question = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    gold_sql = table.Column<string>(type: "text", nullable: false),
                    gold_result_fingerprint = table.Column<string>(type: "text", nullable: true),
                    source_signal_id = table.Column<int>(type: "integer", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_eval_cases", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mcp_eval_runs",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: true),
                    triggered_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    total_cases = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    passed_cases = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    execution_accuracy = table.Column<double>(type: "double precision", nullable: false, defaultValue: 0.0),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    judge_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_eval_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mcp_eval_results",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    eval_run_id = table.Column<int>(type: "integer", nullable: false),
                    eval_case_id = table.Column<int>(type: "integer", nullable: false),
                    generated_sql = table.Column<string>(type: "text", nullable: true),
                    passed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    failure_tag = table.Column<int>(type: "integer", nullable: false),
                    execution_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    judge_used = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    judge_verdict = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    result_row_count = table.Column<int>(type: "integer", nullable: true),
                    execution_time_ms = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_eval_results", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_eval_cases_data_source_id_is_active",
                table: "mcp_eval_cases",
                columns: new[] { "data_source_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_eval_results_eval_case_id",
                table: "mcp_eval_results",
                column: "eval_case_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_eval_results_eval_run_id",
                table: "mcp_eval_results",
                column: "eval_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_eval_runs_created_time",
                table: "mcp_eval_runs",
                column: "created_time");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_eval_runs_project_id",
                table: "mcp_eval_runs",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_eval_cases");

            migrationBuilder.DropTable(
                name: "mcp_eval_results");

            migrationBuilder.DropTable(
                name: "mcp_eval_runs");
        }
    }
}
