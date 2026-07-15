using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.SampleProject.Warp.Migrations
{
    /// <inheritdoc />
    public partial class WarpInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "warp");

            migrationBuilder.CreateTable(
                name: "background_service_definition",
                schema: "warp",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    declared_scope = table.Column<int>(type: "integer", nullable: false),
                    first_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_background_service_definition", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "circuit_breaker_state",
                schema: "warp",
                columns: table => new
                {
                    group_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    failure_count = table.Column<int>(type: "integer", nullable: false),
                    open_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_failure_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_circuit_breaker_state", x => x.group_key);
                });

            migrationBuilder.CreateTable(
                name: "concurrency_limit",
                schema: "warp",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    limit = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_concurrency_limit", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "counter",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_counter", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "job",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "text", nullable: true),
                    message = table.Column<string>(type: "text", nullable: true),
                    create_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    schedule_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    current_state = table.Column<int>(type: "integer", nullable: false),
                    retried_times = table.Column<int>(type: "integer", nullable: false),
                    max_retries = table.Column<int>(type: "integer", nullable: false),
                    queue = table.Column<string>(type: "text", nullable: false),
                    parent_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_worker_id = table.Column<Guid>(type: "uuid", nullable: true),
                    handler_type = table.Column<string>(type: "text", nullable: true),
                    expire_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_keep_alive = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    trace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    spawned_by_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_count = table.Column<int>(type: "integer", nullable: false),
                    continuation_options = table.Column<int>(type: "integer", nullable: true),
                    cancellation_mode = table.Column<int>(type: "integer", nullable: false),
                    metadata = table.Column<string>(type: "text", nullable: true),
                    parent_span_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job", x => x.id);
                    table.ForeignKey(
                        name: "fk_job_job_parent_job_id",
                        column: x => x.parent_job_id,
                        principalSchema: "warp",
                        principalTable: "job",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "job_log",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    level = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    exception = table.Column<string>(type: "text", nullable: true),
                    duration_ms = table.Column<double>(type: "double precision", nullable: true),
                    worker_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    value = table.Column<short>(type: "smallint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rate_limit_bucket",
                schema: "warp",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    window_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    current_count = table.Column<int>(type: "integer", nullable: false),
                    timestamps_json = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rate_limit_bucket", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "rate_limit_override",
                schema: "warp",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    window_seconds = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rate_limit_override", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "recurring_job",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: true),
                    type = table.Column<string>(type: "text", nullable: true),
                    message = table.Column<string>(type: "text", nullable: true),
                    cron = table.Column<string>(type: "text", nullable: true),
                    queue = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_execution = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_execution = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    disabled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recurring_job", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "saga_state",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    correlation_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    state_json = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saga_state", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "server",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_name = table.Column<string>(type: "text", nullable: false),
                    started_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_heartbeat_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    service_count = table.Column<int>(type: "integer", nullable: false),
                    cpu_usage_percent = table.Column<double>(type: "double precision", nullable: true),
                    memory_working_set_bytes = table.Column<long>(type: "bigint", nullable: true),
                    paused_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "statistic",
                schema: "warp",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statistic", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "recurring_job_log",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    recurring_job_id = table.Column<int>(type: "integer", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    skipped = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recurring_job_log", x => x.id);
                    table.ForeignKey(
                        name: "fk_recurring_job_log_job_job_id",
                        column: x => x.job_id,
                        principalSchema: "warp",
                        principalTable: "job",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "saga_job_link",
                schema: "warp",
                columns: table => new
                {
                    saga_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saga_job_link", x => new { x.saga_id, x.job_id });
                    table.ForeignKey(
                        name: "fk_saga_job_link_saga_state_saga_id",
                        column: x => x.saga_id,
                        principalSchema: "warp",
                        principalTable: "saga_state",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "background_service_instance",
                schema: "warp",
                columns: table => new
                {
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    declared_scope = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_heartbeat_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    last_error_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    restart_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_background_service_instance", x => new { x.server_id, x.service_name });
                    table.ForeignKey(
                        name: "fk_background_service_instance_background_service_definition_s",
                        column: x => x.service_name,
                        principalSchema: "warp",
                        principalTable: "background_service_definition",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_background_service_instance_server_server_id",
                        column: x => x.server_id,
                        principalSchema: "warp",
                        principalTable: "server",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "background_service_lease",
                schema: "warp",
                columns: table => new
                {
                    service_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    holder_server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lease_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_background_service_lease", x => x.service_name);
                    table.ForeignKey(
                        name: "fk_background_service_lease_background_service_definition_serv",
                        column: x => x.service_name,
                        principalSchema: "warp",
                        principalTable: "background_service_definition",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_background_service_lease_server_holder_server_id",
                        column: x => x.holder_server_id,
                        principalSchema: "warp",
                        principalTable: "server",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "server_task",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_name = table.Column<string>(type: "text", nullable: false),
                    interval_seconds = table.Column<double>(type: "double precision", nullable: true),
                    last_status = table.Column<string>(type: "text", nullable: true),
                    last_message = table.Column<string>(type: "text", nullable: true),
                    last_run = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_duration_ms = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_task", x => x.id);
                    table.ForeignKey(
                        name: "fk_server_task_server_server_id",
                        column: x => x.server_id,
                        principalSchema: "warp",
                        principalTable: "server",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "worker_group",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    worker_count = table.Column<int>(type: "integer", nullable: false),
                    queues = table.Column<string>(type: "text", nullable: false),
                    polling_interval_ms = table.Column<double>(type: "double precision", nullable: false),
                    paused_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_worker_group", x => x.id);
                    table.ForeignKey(
                        name: "fk_worker_group_server_server_id",
                        column: x => x.server_id,
                        principalSchema: "warp",
                        principalTable: "server",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "background_service_log",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    exception_type = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    exception_message = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_background_service_log", x => x.id);
                    table.ForeignKey(
                        name: "fk_background_service_log_background_service_instance_server_i",
                        columns: x => new { x.server_id, x.service_name },
                        principalSchema: "warp",
                        principalTable: "background_service_instance",
                        principalColumns: new[] { "server_id", "service_name" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_background_service_log_server_server_id",
                        column: x => x.server_id,
                        principalSchema: "warp",
                        principalTable: "server",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "server_log",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_task_id = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", nullable: true),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    duration_ms = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_log", x => x.id);
                    table.ForeignKey(
                        name: "fk_server_log_server_server_id",
                        column: x => x.server_id,
                        principalSchema: "warp",
                        principalTable: "server",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_server_log_server_task_server_task_id",
                        column: x => x.server_task_id,
                        principalSchema: "warp",
                        principalTable: "server_task",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "worker",
                schema: "warp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_heartbeat_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    worker_group_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_worker", x => x.id);
                    table.ForeignKey(
                        name: "fk_worker_server_server_id",
                        column: x => x.server_id,
                        principalSchema: "warp",
                        principalTable: "server",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_worker_worker_group_worker_group_id",
                        column: x => x.worker_group_id,
                        principalSchema: "warp",
                        principalTable: "worker_group",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_background_service_instance_server_id",
                schema: "warp",
                table: "background_service_instance",
                column: "server_id");

            migrationBuilder.CreateIndex(
                name: "ix_background_service_instance_service_name",
                schema: "warp",
                table: "background_service_instance",
                column: "service_name");

            migrationBuilder.CreateIndex(
                name: "ix_background_service_lease_holder_server_id",
                schema: "warp",
                table: "background_service_lease",
                column: "holder_server_id");

            migrationBuilder.CreateIndex(
                name: "ix_background_service_log_server_id_service_name_id",
                schema: "warp",
                table: "background_service_log",
                columns: new[] { "server_id", "service_name", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_background_service_log_service_name_id",
                schema: "warp",
                table: "background_service_log",
                columns: new[] { "service_name", "id" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_circuit_breaker_state_open_until",
                schema: "warp",
                table: "circuit_breaker_state",
                column: "open_until");

            migrationBuilder.CreateIndex(
                name: "ix_counter_key",
                schema: "warp",
                table: "counter",
                column: "key");

            migrationBuilder.CreateIndex(
                name: "ix_job_expire_at",
                schema: "warp",
                table: "job",
                column: "expire_at");

            migrationBuilder.CreateIndex(
                name: "ix_job_kind_current_state_create_time",
                schema: "warp",
                table: "job",
                columns: new[] { "kind", "current_state", "create_time" });

            migrationBuilder.CreateIndex(
                name: "ix_job_kind_current_state_queue_schedule_time",
                schema: "warp",
                table: "job",
                columns: new[] { "kind", "current_state", "queue", "schedule_time" });

            migrationBuilder.CreateIndex(
                name: "ix_job_parent_job_id_current_state",
                schema: "warp",
                table: "job",
                columns: new[] { "parent_job_id", "current_state" });

            migrationBuilder.CreateIndex(
                name: "ix_job_trace_id",
                schema: "warp",
                table: "job",
                column: "trace_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_log_job_id_event_type_timestamp",
                schema: "warp",
                table: "job_log",
                columns: new[] { "job_id", "event_type", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_recurring_job_name",
                schema: "warp",
                table: "recurring_job",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recurring_job_log_job_id",
                schema: "warp",
                table: "recurring_job_log",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_recurring_job_log_recurring_job_id",
                schema: "warp",
                table: "recurring_job_log",
                column: "recurring_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_saga_job_link_saga_id_created_at",
                schema: "warp",
                table: "saga_job_link",
                columns: new[] { "saga_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_saga_state_created_at",
                schema: "warp",
                table: "saga_state",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_saga_state_type_correlation_key",
                schema: "warp",
                table: "saga_state",
                columns: new[] { "type", "correlation_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_server_log_server_id",
                schema: "warp",
                table: "server_log",
                column: "server_id");

            migrationBuilder.CreateIndex(
                name: "ix_server_log_server_task_id",
                schema: "warp",
                table: "server_log",
                column: "server_task_id");

            migrationBuilder.CreateIndex(
                name: "ix_server_log_timestamp",
                schema: "warp",
                table: "server_log",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_server_task_server_id",
                schema: "warp",
                table: "server_task",
                column: "server_id");

            migrationBuilder.CreateIndex(
                name: "ix_worker_server_id",
                schema: "warp",
                table: "worker",
                column: "server_id");

            migrationBuilder.CreateIndex(
                name: "ix_worker_worker_group_id",
                schema: "warp",
                table: "worker",
                column: "worker_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_worker_group_server_id",
                schema: "warp",
                table: "worker_group",
                column: "server_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "background_service_lease",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "background_service_log",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "circuit_breaker_state",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "concurrency_limit",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "counter",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "job_log",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "rate_limit_bucket",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "rate_limit_override",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "recurring_job",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "recurring_job_log",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "saga_job_link",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "server_log",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "statistic",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "worker",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "background_service_instance",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "job",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "saga_state",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "server_task",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "worker_group",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "background_service_definition",
                schema: "warp");

            migrationBuilder.DropTable(
                name: "server",
                schema: "warp");
        }
    }
}
