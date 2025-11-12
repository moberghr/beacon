using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "semantico");

            migrationBuilder.CreateTable(
                name: "data_sources",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    connection_string = table.Column<string>(type: "text", nullable: false),
                    database_engine_type = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_sources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "queries",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    final_query = table.Column<string>(type: "text", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_queries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "recipients",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    destination = table.Column<string>(type: "text", nullable: false),
                    notification_type = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipients", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "database_metadata",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    schema_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    table_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    table_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_refreshed = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_database_metadata", x => x.id);
                    table.ForeignKey(
                        name: "fk_database_metadata_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_jobs",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    query_text = table.Column<string>(type: "text", nullable: false),
                    destination_data_source_id = table.Column<int>(type: "integer", nullable: false),
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
                        name: "fk_migration_jobs_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_migration_jobs_data_sources_destination_data_source_id",
                        column: x => x.destination_data_source_id,
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "query_parameters",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    query_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    placeholder = table.Column<string>(type: "text", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_query_parameters", x => x.id);
                    table.ForeignKey(
                        name: "fk_query_parameters_queries_query_id",
                        column: x => x.query_id,
                        principalSchema: "semantico",
                        principalTable: "queries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "query_steps",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    query_id = table.Column<int>(type: "integer", nullable: false),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    sql_value = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_query_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_query_steps_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_query_steps_queries_query_id",
                        column: x => x.query_id,
                        principalSchema: "semantico",
                        principalTable: "queries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    query_id = table.Column<int>(type: "integer", nullable: false),
                    cron_expression = table.Column<string>(type: "text", nullable: false),
                    max_rows = table.Column<int>(type: "integer", nullable: true),
                    include_attachment = table.Column<bool>(type: "boolean", nullable: false),
                    result_attachment_type = table.Column<int>(type: "integer", nullable: true),
                    show_query = table.Column<bool>(type: "boolean", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: true),
                    store_results = table.Column<bool>(type: "boolean", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscriptions_queries_query_id",
                        column: x => x.query_id,
                        principalSchema: "semantico",
                        principalTable: "queries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "column_metadata",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    database_metadata_id = table.Column<int>(type: "integer", nullable: false),
                    column_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    data_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_nullable = table.Column<bool>(type: "boolean", nullable: false),
                    is_primary_key = table.Column<bool>(type: "boolean", nullable: false),
                    is_foreign_key = table.Column<bool>(type: "boolean", nullable: false),
                    ordinal_position = table.Column<int>(type: "integer", nullable: false),
                    foreign_key_table = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    foreign_key_column = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    default_value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    max_length = table.Column<int>(type: "integer", nullable: true),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_column_metadata", x => x.id);
                    table.ForeignKey(
                        name: "fk_column_metadata_database_metadata_database_metadata_id",
                        column: x => x.database_metadata_id,
                        principalSchema: "semantico",
                        principalTable: "database_metadata",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "index_metadata",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    database_metadata_id = table.Column<int>(type: "integer", nullable: false),
                    index_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_unique = table.Column<bool>(type: "boolean", nullable: false),
                    is_primary_key = table.Column<bool>(type: "boolean", nullable: false),
                    columns = table.Column<string[]>(type: "text[]", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_index_metadata", x => x.id);
                    table.ForeignKey(
                        name: "fk_index_metadata_database_metadata_database_metadata_id",
                        column: x => x.database_metadata_id,
                        principalSchema: "semantico",
                        principalTable: "database_metadata",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
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
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_migration_executions_migration_jobs_migration_job_id",
                        column: x => x.migration_job_id,
                        principalSchema: "semantico",
                        principalTable: "migration_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "query_step_parameters",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    query_step_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    placeholder = table.Column<string>(type: "text", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_query_step_parameters", x => x.id);
                    table.ForeignKey(
                        name: "fk_query_step_parameters_query_steps_query_step_id",
                        column: x => x.query_step_id,
                        principalSchema: "semantico",
                        principalTable: "query_steps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "query_execution_history",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    subscription_id = table.Column<int>(type: "integer", nullable: false),
                    result_count = table.Column<int>(type: "integer", nullable: false),
                    compiled_sql = table.Column<string>(type: "text", nullable: false),
                    notification_status = table.Column<int>(type: "integer", nullable: false),
                    execution_time_ms = table.Column<double>(type: "double precision", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_query_execution_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_query_execution_history_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "semantico",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recipient_subscription",
                schema: "semantico",
                columns: table => new
                {
                    recipients_id = table.Column<int>(type: "integer", nullable: false),
                    subscriptions_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipient_subscription", x => new { x.recipients_id, x.subscriptions_id });
                    table.ForeignKey(
                        name: "fk_recipient_subscription_recipients_recipients_id",
                        column: x => x.recipients_id,
                        principalSchema: "semantico",
                        principalTable: "recipients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_recipient_subscription_subscriptions_subscriptions_id",
                        column: x => x.subscriptions_id,
                        principalSchema: "semantico",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subscription_parameters",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    subscription_id = table.Column<int>(type: "integer", nullable: false),
                    query_placeholder = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_parameters", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscription_parameters_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "semantico",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    query_execution_history_id = table.Column<int>(type: "integer", nullable: false),
                    recipient_id = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    results = table.Column<string>(type: "text", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_query_execution_history_query_execution_histo",
                        column: x => x.query_execution_history_id,
                        principalSchema: "semantico",
                        principalTable: "query_execution_history",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notifications_recipients_recipient_id",
                        column: x => x.recipient_id,
                        principalSchema: "semantico",
                        principalTable: "recipients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_column_metadata_database_metadata_id",
                schema: "semantico",
                table: "column_metadata",
                column: "database_metadata_id");

            migrationBuilder.CreateIndex(
                name: "ix_column_metadata_database_metadata_id_column_name",
                schema: "semantico",
                table: "column_metadata",
                columns: new[] { "database_metadata_id", "column_name" });

            migrationBuilder.CreateIndex(
                name: "ix_database_metadata_data_source_id",
                schema: "semantico",
                table: "database_metadata",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_database_metadata_data_source_id_schema_name_table_name",
                schema: "semantico",
                table: "database_metadata",
                columns: new[] { "data_source_id", "schema_name", "table_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_database_metadata_last_refreshed",
                schema: "semantico",
                table: "database_metadata",
                column: "last_refreshed");

            migrationBuilder.CreateIndex(
                name: "ix_index_metadata_database_metadata_id",
                schema: "semantico",
                table: "index_metadata",
                column: "database_metadata_id");

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
                name: "ix_migration_jobs_data_source_id",
                schema: "semantico",
                table: "migration_jobs",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_migration_jobs_destination_data_source_id",
                schema: "semantico",
                table: "migration_jobs",
                column: "destination_data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_migration_jobs_is_enabled_archived_time",
                schema: "semantico",
                table: "migration_jobs",
                columns: new[] { "is_enabled", "archived_time" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_query_execution_history_id",
                schema: "semantico",
                table: "notifications",
                column: "query_execution_history_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_id",
                schema: "semantico",
                table: "notifications",
                column: "recipient_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_execution_history_subscription_id",
                schema: "semantico",
                table: "query_execution_history",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_parameters_query_id",
                schema: "semantico",
                table: "query_parameters",
                column: "query_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_step_parameters_query_step_id",
                schema: "semantico",
                table: "query_step_parameters",
                column: "query_step_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_steps_data_source_id",
                schema: "semantico",
                table: "query_steps",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_steps_query_id",
                schema: "semantico",
                table: "query_steps",
                column: "query_id");

            migrationBuilder.CreateIndex(
                name: "ix_recipient_subscription_subscriptions_id",
                schema: "semantico",
                table: "recipient_subscription",
                column: "subscriptions_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_parameters_subscription_id",
                schema: "semantico",
                table: "subscription_parameters",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_query_id",
                schema: "semantico",
                table: "subscriptions",
                column: "query_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "column_metadata",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "index_metadata",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "migration_executions",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "query_parameters",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "query_step_parameters",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "recipient_subscription",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "subscription_parameters",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "database_metadata",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "migration_jobs",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "query_execution_history",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "query_steps",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "recipients",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "data_sources",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "queries",
                schema: "semantico");
        }
    }
}
