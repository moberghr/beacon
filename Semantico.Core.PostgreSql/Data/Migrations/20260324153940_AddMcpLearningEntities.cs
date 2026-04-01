using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpLearningEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mcp_documentation_patches",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    target_type = table.Column<int>(type: "integer", nullable: false),
                    target_identifier = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    current_content = table.Column<string>(type: "text", nullable: true),
                    proposed_content = table.Column<string>(type: "text", nullable: false),
                    reasoning = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    supporting_signal_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    applied_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    applied_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_documentation_patches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mcp_learned_patterns",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    schema_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    table_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    column_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    pattern_type = table.Column<int>(type: "integer", nullable: false),
                    pattern_content = table.Column<string>(type: "text", nullable: false),
                    example_question = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    example_sql = table.Column<string>(type: "text", nullable: true),
                    signal_count = table.Column<int>(type: "integer", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    reviewed_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_refreshed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_learned_patterns", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mcp_query_signals",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: true),
                    data_source_id = table.Column<int>(type: "integer", nullable: true),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    tool = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    question = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    intent_classification = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    routing_decision = table.Column<string>(type: "text", nullable: true),
                    generated_sql = table.Column<string>(type: "text", nullable: true),
                    tables_used = table.Column<string>(type: "text", nullable: true),
                    columns_used = table.Column<string>(type: "text", nullable: true),
                    schema_validation_failed = table.Column<bool>(type: "boolean", nullable: false),
                    schema_validation_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    execution_failed = table.Column<bool>(type: "boolean", nullable: false),
                    execution_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    retry_attempted = table.Column<bool>(type: "boolean", nullable: false),
                    retry_succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    corrected_sql = table.Column<string>(type: "text", nullable: true),
                    result_row_count = table.Column<int>(type: "integer", nullable: true),
                    execution_time_ms = table.Column<int>(type: "integer", nullable: false),
                    is_successful = table.Column<bool>(type: "boolean", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_query_signals", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_documentation_patches_data_source_id",
                schema: "semantico",
                table: "mcp_documentation_patches",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_documentation_patches_project_id_status",
                schema: "semantico",
                table: "mcp_documentation_patches",
                columns: new[] { "project_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_learned_patterns_data_source_id_status_table_name",
                schema: "semantico",
                table: "mcp_learned_patterns",
                columns: new[] { "data_source_id", "status", "table_name" });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_learned_patterns_project_id",
                schema: "semantico",
                table: "mcp_learned_patterns",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_query_signals_created_time",
                schema: "semantico",
                table: "mcp_query_signals",
                column: "created_time");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_query_signals_data_source_id",
                schema: "semantico",
                table: "mcp_query_signals",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_query_signals_is_successful",
                schema: "semantico",
                table: "mcp_query_signals",
                column: "is_successful");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_query_signals_project_id",
                schema: "semantico",
                table: "mcp_query_signals",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_documentation_patches",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "mcp_learned_patterns",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "mcp_query_signals",
                schema: "semantico");
        }
    }
}
