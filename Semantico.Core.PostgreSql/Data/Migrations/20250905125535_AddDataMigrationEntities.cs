using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDataMigrationEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "migration_jobs",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    query_text = table.Column<string>(type: "text", nullable: false),
                    destination_project_id = table.Column<int>(type: "integer", nullable: false),
                    destination_table = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    mode = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    schedule = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    max_retries = table.Column<int>(type: "integer", nullable: false),
                    timeout_minutes = table.Column<int>(type: "integer", nullable: false),
                    validate_before_execution = table.Column<bool>(type: "boolean", nullable: false),
                    transformation_script = table.Column<string>(type: "text", nullable: true),
                    changed_by = table.Column<string>(type: "text", nullable: true),
                    changed_on = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_migration_jobs_projects_destination_project_id",
                        column: x => x.destination_project_id,
                        principalSchema: "semantico",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_migration_jobs_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "semantico",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_executions",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    migration_job_id = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    source_rows_read = table.Column<int>(type: "integer", nullable: false),
                    destination_rows_written = table.Column<int>(type: "integer", nullable: false),
                    rows_skipped = table.Column<int>(type: "integer", nullable: false),
                    rows_failed = table.Column<int>(type: "integer", nullable: false),
                    executed_query = table.Column<string>(type: "text", nullable: false),
                    query_parameters = table.Column<string>(type: "text", nullable: true),
                    transformation_applied = table.Column<string>(type: "text", nullable: true),
                    retry_attempt = table.Column<int>(type: "integer", nullable: false),
                    parent_execution_id = table.Column<int>(type: "integer", nullable: true),
                    estimated_total_rows = table.Column<int>(type: "integer", nullable: true),
                    processed_rows = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_executions", x => x.id);
                    table.ForeignKey(
                        name: "fk_migration_executions_migration_executions_parent_execution_",
                        column: x => x.parent_execution_id,
                        principalSchema: "semantico",
                        principalTable: "migration_executions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_migration_executions_migration_jobs_migration_job_id",
                        column: x => x.migration_job_id,
                        principalSchema: "semantico",
                        principalTable: "migration_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_migration_executions_migration_job_id",
                schema: "semantico",
                table: "migration_executions",
                column: "migration_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_migration_executions_parent_execution_id",
                schema: "semantico",
                table: "migration_executions",
                column: "parent_execution_id");

            migrationBuilder.CreateIndex(
                name: "ix_migration_executions_started_at",
                schema: "semantico",
                table: "migration_executions",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_migration_executions_status_started_at",
                schema: "semantico",
                table: "migration_executions",
                columns: new[] { "status", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_jobs_destination_project_id",
                schema: "semantico",
                table: "migration_jobs",
                column: "destination_project_id");

            migrationBuilder.CreateIndex(
                name: "ix_migration_jobs_is_enabled_archived_time",
                schema: "semantico",
                table: "migration_jobs",
                columns: new[] { "is_enabled", "archived_time" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_jobs_project_id",
                schema: "semantico",
                table: "migration_jobs",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "migration_executions",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "migration_jobs",
                schema: "semantico");
        }
    }
}
