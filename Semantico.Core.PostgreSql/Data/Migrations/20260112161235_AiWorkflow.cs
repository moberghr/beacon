using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allow_sample_data_for_ai",
                schema: "semantico",
                table: "data_sources",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "documentation_agent_runs",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    documentation_id = table.Column<int>(type: "integer", nullable: true),
                    started_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    current_phase = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    progress_percent = table.Column<int>(type: "integer", nullable: false),
                    progress_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    total_tables_discovered = table.Column<int>(type: "integer", nullable: false),
                    discovered_tables_json = table.Column<string>(type: "text", nullable: true),
                    domain_groups_json = table.Column<string>(type: "text", nullable: true),
                    tables_completed = table.Column<int>(type: "integer", nullable: false),
                    tables_failed = table.Column<int>(type: "integer", nullable: false),
                    completed_tables_json = table.Column<string>(type: "text", nullable: true),
                    failed_tables_json = table.Column<string>(type: "text", nullable: true),
                    current_batch_index = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    total_tokens_used = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric", nullable: false),
                    last_checkpoint_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    checkpoint_state_json = table.Column<string>(type: "text", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documentation_agent_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_documentation_agent_runs_data_source_documentations_documen",
                        column: x => x.documentation_id,
                        principalSchema: "semantico",
                        principalTable: "data_source_documentations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_documentation_agent_runs_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_documentation_agent_runs_current_phase",
                schema: "semantico",
                table: "documentation_agent_runs",
                column: "current_phase");

            migrationBuilder.CreateIndex(
                name: "ix_documentation_agent_runs_data_source_id",
                schema: "semantico",
                table: "documentation_agent_runs",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_documentation_agent_runs_data_source_id_status",
                schema: "semantico",
                table: "documentation_agent_runs",
                columns: new[] { "data_source_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_documentation_agent_runs_documentation_id",
                schema: "semantico",
                table: "documentation_agent_runs",
                column: "documentation_id");

            migrationBuilder.CreateIndex(
                name: "ix_documentation_agent_runs_started_at",
                schema: "semantico",
                table: "documentation_agent_runs",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_documentation_agent_runs_status",
                schema: "semantico",
                table: "documentation_agent_runs",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "documentation_agent_runs",
                schema: "semantico");

            migrationBuilder.DropColumn(
                name: "allow_sample_data_for_ai",
                schema: "semantico",
                table: "data_sources");
        }
    }
}
