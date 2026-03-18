using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProjectDocs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "documentation_agent_runs",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "documentation_sections",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "documentation_versions",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "project_reports",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "data_source_documentations",
                schema: "semantico");

            migrationBuilder.CreateTable(
                name: "project_documentations",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    generated_by_model = table.Column<string>(type: "text", nullable: false),
                    generated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    generated_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric", nullable: false),
                    data_sources_analyzed = table.Column<int>(type: "integer", nullable: false),
                    tables_analyzed = table.Column<int>(type: "integer", nullable: false),
                    code_references_analyzed = table.Column<int>(type: "integer", nullable: false),
                    generation_duration = table.Column<TimeSpan>(type: "interval", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_documentations", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_documentations_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "semantico",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_documentation_sections",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_documentation_id = table.Column<int>(type: "integer", nullable: false),
                    section_type = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_documentation_sections", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_documentation_sections_project_documentations_proje",
                        column: x => x.project_documentation_id,
                        principalSchema: "semantico",
                        principalTable: "project_documentations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_documentation_sections_project_documentation_id",
                schema: "semantico",
                table: "project_documentation_sections",
                column: "project_documentation_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_documentation_sections_section_type",
                schema: "semantico",
                table: "project_documentation_sections",
                column: "section_type");

            migrationBuilder.CreateIndex(
                name: "ix_project_documentations_generated_at",
                schema: "semantico",
                table: "project_documentations",
                column: "generated_at");

            migrationBuilder.CreateIndex(
                name: "ix_project_documentations_project_id",
                schema: "semantico",
                table: "project_documentations",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_documentation_sections",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "project_documentations",
                schema: "semantico");

            migrationBuilder.CreateTable(
                name: "data_source_documentations",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric", nullable: false),
                    generated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    generated_by_model = table.Column<string>(type: "text", nullable: false),
                    generated_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    last_modified_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    metadata = table.Column<string>(type: "text", nullable: true),
                    modified_by = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    tables_analyzed = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    tokens_used = table.Column<int>(type: "integer", nullable: false)
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
                name: "project_reports",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "text", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    generated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    report_format = table.Column<int>(type: "integer", nullable: false),
                    report_type = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_reports_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "semantico",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "documentation_agent_runs",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    documentation_id = table.Column<int>(type: "integer", nullable: true),
                    checkpoint_state_json = table.Column<string>(type: "text", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_tables_json = table.Column<string>(type: "text", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    current_batch_index = table.Column<int>(type: "integer", nullable: false),
                    current_phase = table.Column<int>(type: "integer", nullable: false),
                    discovered_tables_json = table.Column<string>(type: "text", nullable: true),
                    domain_groups_json = table.Column<string>(type: "text", nullable: true),
                    estimated_cost = table.Column<decimal>(type: "numeric", nullable: false),
                    failed_tables_json = table.Column<string>(type: "text", nullable: true),
                    last_checkpoint_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    progress_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    progress_percent = table.Column<int>(type: "integer", nullable: false),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    tables_completed = table.Column<int>(type: "integer", nullable: false),
                    tables_failed = table.Column<int>(type: "integer", nullable: false),
                    total_tables_discovered = table.Column<int>(type: "integer", nullable: false),
                    total_tokens_used = table.Column<int>(type: "integer", nullable: false)
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
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "documentation_sections",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    documentation_id = table.Column<int>(type: "integer", nullable: false),
                    ai_generated_content = table.Column<string>(type: "text", nullable: false),
                    content_format = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_user_edited = table.Column<bool>(type: "boolean", nullable: false),
                    modified_by = table.Column<string>(type: "text", nullable: false),
                    section_type = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    table_name = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "text", nullable: true),
                    user_edited_content = table.Column<string>(type: "text", nullable: true)
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
                    change_description = table.Column<string>(type: "text", nullable: true),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sections_count = table.Column<int>(type: "integer", nullable: false),
                    snapshot_json = table.Column<string>(type: "text", nullable: false),
                    tokens_used = table.Column<int>(type: "integer", nullable: true),
                    version_number = table.Column<int>(type: "integer", nullable: false)
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
                name: "ix_documentation_agent_runs_current_phase",
                schema: "semantico",
                table: "documentation_agent_runs",
                column: "current_phase");

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
                name: "ix_documentation_versions_created_time",
                schema: "semantico",
                table: "documentation_versions",
                column: "created_time");

            migrationBuilder.CreateIndex(
                name: "ix_documentation_versions_documentation_id",
                schema: "semantico",
                table: "documentation_versions",
                column: "documentation_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_reports_generated_at",
                schema: "semantico",
                table: "project_reports",
                column: "generated_at");

            migrationBuilder.CreateIndex(
                name: "ix_project_reports_project_id",
                schema: "semantico",
                table: "project_reports",
                column: "project_id");
        }
    }
}
