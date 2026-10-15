using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.SampleProject.Warp.Migrations
{
    /// <inheritdoc />
    public partial class WarpSchemaUpdate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "max_retries",
                schema: "warp",
                table: "job");

            migrationBuilder.DropColumn(
                name: "retried_times",
                schema: "warp",
                table: "job");

            migrationBuilder.AddColumn<string>(
                name: "application",
                schema: "warp",
                table: "server",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "environment",
                schema: "warp",
                table: "server",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "version",
                schema: "warp",
                table: "server",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "application",
                schema: "warp",
                table: "job",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "adapter_call_log",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    adapter_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    operation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    group_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    duration_ms = table.Column<double>(type: "double precision", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    exception_type = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    exception_message = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    request_summary = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    request_headers = table.Column<string>(type: "text", nullable: true),
                    response_headers = table.Column<string>(type: "text", nullable: true),
                    request_body = table.Column<string>(type: "text", nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    machine_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    trace_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    tags_json = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    expire_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_adapter_call_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "adapter_definition",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    first_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    config_summary = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    call_log_retention_count = table.Column<int>(type: "integer", nullable: true),
                    group_label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    shared_policy_json = table.Column<string>(type: "text", nullable: true),
                    shared_policy_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    has_policy_conflict = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_adapter_definition", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "application_instance",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    machine_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_heartbeat_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    cpu_usage_percent = table.Column<double>(type: "double precision", nullable: true),
                    memory_working_set_bytes = table.Column<long>(type: "bigint", nullable: true),
                    version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    environment = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_instance", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "application_instance_log",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    event_type = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    expire_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_instance_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "client_event_log",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    type = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    level = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    message = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    stack = table.Column<string>(type: "text", nullable: true),
                    value = table.Column<double>(type: "double precision", nullable: true),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    trace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    release = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    remote_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    breadcrumbs = table.Column<string>(type: "text", nullable: true),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expire_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_event_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "endpoint_call_log",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    route_template = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    operation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    group_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    duration_ms = table.Column<double>(type: "double precision", nullable: false),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    remote_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    session = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    user = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    exception_type = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    exception_message = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    request_headers = table.Column<string>(type: "text", nullable: true),
                    response_headers = table.Column<string>(type: "text", nullable: true),
                    request_body = table.Column<string>(type: "text", nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    machine_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    trace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tags_json = table.Column<string>(type: "text", nullable: true),
                    expire_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_endpoint_call_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "error_group",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    exception_type = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    culprit = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    application = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    first_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    count = table.Column<long>(type: "bigint", nullable: false),
                    last_sample = table.Column<string>(type: "text", nullable: true),
                    sample_trace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    first_seen_version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    last_seen_version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    environment = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    recent_samples = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    status_changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expire_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_error_group", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "error_occurrence",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    exception_type = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    message = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    stack = table.Column<string>(type: "text", nullable: true),
                    culprit = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    trace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    application = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    environment = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_error_occurrence", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "slo_definition",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    dimension = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    application = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    target_value = table.Column<double>(type: "double precision", nullable: false),
                    percentile = table.Column<int>(type: "integer", nullable: true),
                    window_seconds = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_slo_definition", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "slo_evaluation",
                schema: "warp",
                columns: table => new
                {
                    slo_definition_id = table.Column<int>(type: "integer", nullable: false),
                    attainment = table.Column<double>(type: "double precision", nullable: false),
                    budget_remaining = table.Column<double>(type: "double precision", nullable: false),
                    burn_rate_short = table.Column<double>(type: "double precision", nullable: false),
                    burn_rate_long = table.Column<double>(type: "double precision", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    acknowledged_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_evaluated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_slo_evaluation", x => x.slo_definition_id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_delivery",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    event_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    headers_json = table.Column<string>(type: "text", nullable: true),
                    group_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    signing_mode = table.Column<int>(type: "integer", nullable: false),
                    secret = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    retry_schedule = table.Column<string>(type: "text", nullable: false),
                    success_codes_json = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    exhausted_callback_pending = table.Column<bool>(type: "boolean", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expire_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_delivery", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_job_current_state_schedule_time",
                schema: "warp",
                table: "job",
                columns: new[] { "current_state", "schedule_time" });

            migrationBuilder.CreateIndex(
                name: "ix_adapter_call_log_adapter_name_correlation_id",
                schema: "warp",
                table: "adapter_call_log",
                columns: new[] { "adapter_name", "correlation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_adapter_call_log_adapter_name_group_name_timestamp",
                schema: "warp",
                table: "adapter_call_log",
                columns: new[] { "adapter_name", "group_name", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_adapter_call_log_adapter_name_timestamp",
                schema: "warp",
                table: "adapter_call_log",
                columns: new[] { "adapter_name", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_adapter_call_log_expire_at",
                schema: "warp",
                table: "adapter_call_log",
                column: "expire_at");

            migrationBuilder.CreateIndex(
                name: "ix_adapter_definition_last_seen_at",
                schema: "warp",
                table: "adapter_definition",
                column: "last_seen_at");

            migrationBuilder.CreateIndex(
                name: "ix_adapter_definition_name",
                schema: "warp",
                table: "adapter_definition",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_application_instance_application_name",
                schema: "warp",
                table: "application_instance",
                column: "application_name");

            migrationBuilder.CreateIndex(
                name: "ix_application_instance_last_heartbeat_at",
                schema: "warp",
                table: "application_instance",
                column: "last_heartbeat_at");

            migrationBuilder.CreateIndex(
                name: "ix_application_instance_log_application_name_timestamp",
                schema: "warp",
                table: "application_instance_log",
                columns: new[] { "application_name", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_application_instance_log_expire_at",
                schema: "warp",
                table: "application_instance_log",
                column: "expire_at");

            migrationBuilder.CreateIndex(
                name: "ix_application_instance_log_instance_id_timestamp",
                schema: "warp",
                table: "application_instance_log",
                columns: new[] { "instance_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_client_event_log_application_timestamp",
                schema: "warp",
                table: "client_event_log",
                columns: new[] { "application", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_client_event_log_expire_at",
                schema: "warp",
                table: "client_event_log",
                column: "expire_at");

            migrationBuilder.CreateIndex(
                name: "ix_client_event_log_session_id_timestamp",
                schema: "warp",
                table: "client_event_log",
                columns: new[] { "session_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_client_event_log_type_timestamp",
                schema: "warp",
                table: "client_event_log",
                columns: new[] { "type", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_endpoint_call_log_expire_at",
                schema: "warp",
                table: "endpoint_call_log",
                column: "expire_at");

            migrationBuilder.CreateIndex(
                name: "ix_endpoint_call_log_method_route_template_timestamp",
                schema: "warp",
                table: "endpoint_call_log",
                columns: new[] { "method", "route_template", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_endpoint_call_log_session_timestamp",
                schema: "warp",
                table: "endpoint_call_log",
                columns: new[] { "session", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_endpoint_call_log_trace_id",
                schema: "warp",
                table: "endpoint_call_log",
                column: "trace_id");

            migrationBuilder.CreateIndex(
                name: "ix_error_group_expire_at",
                schema: "warp",
                table: "error_group",
                column: "expire_at");

            migrationBuilder.CreateIndex(
                name: "ix_error_group_fingerprint",
                schema: "warp",
                table: "error_group",
                column: "fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_error_group_source_status_last_seen_at",
                schema: "warp",
                table: "error_group",
                columns: new[] { "source", "status", "last_seen_at" });

            migrationBuilder.CreateIndex(
                name: "ix_error_occurrence_timestamp",
                schema: "warp",
                table: "error_occurrence",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_slo_definition_kind_dimension_application",
                schema: "warp",
                table: "slo_definition",
                columns: new[] { "kind", "dimension", "application" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_created_at",
                schema: "warp",
                table: "webhook_delivery",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_event_type_created_at",
                schema: "warp",
                table: "webhook_delivery",
                columns: new[] { "event_type", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_expire_at",
                schema: "warp",
                table: "webhook_delivery",
                column: "expire_at");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_reference",
                schema: "warp",
                table: "webhook_delivery",
                column: "reference");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_status_next_attempt_at",
                schema: "warp",
                table: "webhook_delivery",
                columns: new[] { "status", "next_attempt_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adapter_call_log",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "adapter_definition",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "application_instance",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "application_instance_log",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "client_event_log",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "endpoint_call_log",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "error_group",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "error_occurrence",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "slo_definition",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "slo_evaluation",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "webhook_delivery",
                schema: "warp");

            migrationBuilder.DropIndex(
                name: "ix_job_current_state_schedule_time",
                schema: "warp",
                table: "job");

            migrationBuilder.DropColumn(
                name: "application",
                schema: "warp",
                table: "server");

            migrationBuilder.DropColumn(
                name: "environment",
                schema: "warp",
                table: "server");

            migrationBuilder.DropColumn(
                name: "version",
                schema: "warp",
                table: "server");

            migrationBuilder.DropColumn(
                name: "application",
                schema: "warp",
                table: "job");

            migrationBuilder.AddColumn<int>(
                name: "max_retries",
                schema: "warp",
                table: "job",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "retried_times",
                schema: "warp",
                table: "job",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
