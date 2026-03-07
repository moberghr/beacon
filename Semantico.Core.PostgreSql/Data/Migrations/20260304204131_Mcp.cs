using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class Mcp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_key_credentials",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    key_prefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scopes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    allowed_data_source_ids = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_revoked = table.Column<bool>(type: "boolean", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_key_credentials", x => x.id);
                    table.ForeignKey(
                        name: "fk_api_key_credentials_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "semantico",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "schema_changes",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    detected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    change_type = table.Column<int>(type: "integer", nullable: false),
                    schema_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    table_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    column_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    old_value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    new_value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schema_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_schema_changes_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "schema_snapshots",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    captured_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    schema_json = table.Column<string>(type: "text", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schema_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "fk_schema_snapshots_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mcp_sessions",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    api_key_id = table.Column<int>(type: "integer", nullable: true),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    session_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_activity_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tables_explored = table.Column<string>(type: "text", nullable: true),
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
                        principalSchema: "semantico",
                        principalTable: "api_key_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_mcp_sessions_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "semantico",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "git_hub_repositories",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    repository_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    encrypted_access_token = table.Column<string>(type: "text", nullable: true),
                    last_scan_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    scan_status = table.Column<int>(type: "integer", nullable: false),
                    scan_cron_expression = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_scan_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    total_files_scanned = table.Column<int>(type: "integer", nullable: false),
                    total_references_found = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_git_hub_repositories", x => x.id);
                    table.ForeignKey(
                        name: "fk_git_hub_repositories_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "semantico",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_data_sources",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_data_sources", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_data_sources_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_data_sources_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "semantico",
                        principalTable: "projects",
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
                    report_type = table.Column<int>(type: "integer", nullable: false),
                    report_format = table.Column<int>(type: "integer", nullable: false),
                    generated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    content = table.Column<string>(type: "text", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "mcp_audit_logs",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<int>(type: "integer", nullable: true),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    tool = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    parameters = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    data_source_id = table.Column<int>(type: "integer", nullable: true),
                    execution_time_ms = table.Column<int>(type: "integer", nullable: false),
                    result_row_count = table.Column<int>(type: "integer", nullable: true),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_audit_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_mcp_audit_logs_mcp_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "semantico",
                        principalTable: "mcp_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_mcp_audit_logs_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "semantico",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "code_references",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    git_hub_repository_id = table.Column<int>(type: "integer", nullable: false),
                    file_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: true),
                    reference_type = table.Column<int>(type: "integer", nullable: false),
                    schema_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    table_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    column_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    code_snippet = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    class_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    method_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_references", x => x.id);
                    table.ForeignKey(
                        name: "fk_code_references_git_hub_repositories_git_hub_repository_id",
                        column: x => x.git_hub_repository_id,
                        principalSchema: "semantico",
                        principalTable: "git_hub_repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_key_credentials_is_revoked",
                schema: "semantico",
                table: "api_key_credentials",
                column: "is_revoked");

            migrationBuilder.CreateIndex(
                name: "ix_api_key_credentials_key_hash",
                schema: "semantico",
                table: "api_key_credentials",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_key_credentials_key_prefix",
                schema: "semantico",
                table: "api_key_credentials",
                column: "key_prefix");

            migrationBuilder.CreateIndex(
                name: "ix_api_key_credentials_user_id",
                schema: "semantico",
                table: "api_key_credentials",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_code_references_git_hub_repository_id",
                schema: "semantico",
                table: "code_references",
                column: "git_hub_repository_id");

            migrationBuilder.CreateIndex(
                name: "ix_code_references_reference_type",
                schema: "semantico",
                table: "code_references",
                column: "reference_type");

            migrationBuilder.CreateIndex(
                name: "ix_code_references_schema_name_table_name",
                schema: "semantico",
                table: "code_references",
                columns: new[] { "schema_name", "table_name" });

            migrationBuilder.CreateIndex(
                name: "ix_git_hub_repositories_project_id",
                schema: "semantico",
                table: "git_hub_repositories",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_git_hub_repositories_scan_status",
                schema: "semantico",
                table: "git_hub_repositories",
                column: "scan_status");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_audit_logs_created_time",
                schema: "semantico",
                table: "mcp_audit_logs",
                column: "created_time");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_audit_logs_session_id",
                schema: "semantico",
                table: "mcp_audit_logs",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_audit_logs_tool",
                schema: "semantico",
                table: "mcp_audit_logs",
                column: "tool");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_audit_logs_user_id",
                schema: "semantico",
                table: "mcp_audit_logs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_sessions_api_key_id",
                schema: "semantico",
                table: "mcp_sessions",
                column: "api_key_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_sessions_last_activity_at",
                schema: "semantico",
                table: "mcp_sessions",
                column: "last_activity_at");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_sessions_session_id",
                schema: "semantico",
                table: "mcp_sessions",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mcp_sessions_user_id",
                schema: "semantico",
                table: "mcp_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_data_sources_data_source_id",
                schema: "semantico",
                table: "project_data_sources",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_data_sources_project_id_data_source_id",
                schema: "semantico",
                table: "project_data_sources",
                columns: new[] { "project_id", "data_source_id" },
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "ix_projects_name",
                schema: "semantico",
                table: "projects",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_schema_changes_change_type",
                schema: "semantico",
                table: "schema_changes",
                column: "change_type");

            migrationBuilder.CreateIndex(
                name: "ix_schema_changes_data_source_id",
                schema: "semantico",
                table: "schema_changes",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_schema_changes_detected_at",
                schema: "semantico",
                table: "schema_changes",
                column: "detected_at");

            migrationBuilder.CreateIndex(
                name: "ix_schema_snapshots_captured_at",
                schema: "semantico",
                table: "schema_snapshots",
                column: "captured_at");

            migrationBuilder.CreateIndex(
                name: "ix_schema_snapshots_data_source_id",
                schema: "semantico",
                table: "schema_snapshots",
                column: "data_source_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_references",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "mcp_audit_logs",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "project_data_sources",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "project_reports",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "schema_changes",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "schema_snapshots",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "git_hub_repositories",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "mcp_sessions",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "projects",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "api_key_credentials",
                schema: "semantico");
        }
    }
}
