using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_prompt_templates",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    operation_type = table.Column<int>(type: "integer", nullable: false),
                    prompt_template = table.Column<string>(type: "text", nullable: false),
                    system_prompt = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    temperature = table.Column<decimal>(type: "numeric", nullable: false),
                    max_tokens = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    modified_by = table.Column<string>(type: "text", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_prompt_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "app_setting_history",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    setting_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    old_value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    new_value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    changed_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_setting_history", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "app_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    is_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "comments",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    entity_type = table.Column<int>(type: "integer", nullable: false),
                    entity_id = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    user_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dashboards",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_by_user_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_shared = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    refresh_interval_seconds = table.Column<int>(type: "integer", nullable: true),
                    layout_configuration = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dashboards", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "data_sources",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    data_source_type = table.Column<int>(type: "integer", nullable: false),
                    encrypted_connection_data = table.Column<string>(type: "text", nullable: false),
                    database_engine_type = table.Column<int>(type: "integer", nullable: true),
                    metadata_loading_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    metadata_max_tables = table.Column<int>(type: "integer", nullable: false),
                    metadata_max_columns_per_table = table.Column<int>(type: "integer", nullable: false),
                    metadata_load_table_names_only = table.Column<bool>(type: "boolean", nullable: false),
                    metadata_exclude_schemas = table.Column<string>(type: "text", nullable: true),
                    metadata_include_schemas = table.Column<string>(type: "text", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_sources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mcp_documentation_patches",
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

            migrationBuilder.CreateTable(
                name: "mcp_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ask_system_prompt = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    global_instruction = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    get_context_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    search_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    query_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    get_documentation_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ask_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    max_row_limit = table.Column<int>(type: "integer", nullable: false),
                    enforce_read_only = table.Column<bool>(type: "boolean", nullable: false),
                    enable_pii_detection = table.Column<bool>(type: "boolean", nullable: false),
                    custom_pii_patterns = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    enable_learning = table.Column<bool>(type: "boolean", nullable: false),
                    learning_auto_approve_threshold = table.Column<double>(type: "double precision", nullable: false),
                    learning_injection_budget_chars = table.Column<int>(type: "integer", nullable: false),
                    learning_signal_retention_days = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "query_folders",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    parent_folder_id = table.Column<int>(type: "integer", nullable: true),
                    path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_query_folders", x => x.id);
                    table.ForeignKey(
                        name: "fk_query_folders_query_folders_parent_folder_id",
                        column: x => x.parent_folder_id,
                        principalTable: "query_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recipients",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    destination = table.Column<string>(type: "text", nullable: false),
                    notification_type = table.Column<int>(type: "integer", nullable: false),
                    headers_json = table.Column<string>(type: "text", nullable: true),
                    body_template = table.Column<string>(type: "text", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipients", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_system_role = table.Column<bool>(type: "boolean", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    identity_provider = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    user_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_internal_user = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    password_salt = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_super_admin = table.Column<bool>(type: "boolean", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_login_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dashboard_permissions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    dashboard_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    permission_level = table.Column<int>(type: "integer", nullable: false),
                    granted_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    granted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dashboard_permissions", x => x.id);
                    table.ForeignKey(
                        name: "fk_dashboard_permissions_dashboards_dashboard_id",
                        column: x => x.dashboard_id,
                        principalTable: "dashboards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dashboard_widgets",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    dashboard_id = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    widget_type = table.Column<int>(type: "integer", nullable: false),
                    configuration_json = table.Column<string>(type: "text", nullable: false),
                    position_x = table.Column<int>(type: "integer", nullable: false),
                    position_y = table.Column<int>(type: "integer", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    refresh_interval_seconds = table.Column<int>(type: "integer", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dashboard_widgets", x => x.id);
                    table.ForeignKey(
                        name: "fk_dashboard_widgets_dashboards_dashboard_id",
                        column: x => x.dashboard_id,
                        principalTable: "dashboards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_actors",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    instructions = table.Column<string>(type: "text", nullable: false),
                    additional_context = table.Column<string>(type: "text", nullable: true),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    max_queries = table.Column<int>(type: "integer", nullable: false),
                    max_subscriptions_per_query = table.Column<int>(type: "integer", nullable: false),
                    requires_approval = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    total_tokens_used = table.Column<int>(type: "integer", nullable: false),
                    total_cost = table.Column<decimal>(type: "numeric", nullable: false),
                    last_think_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    think_count = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_actors", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_actors_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "data_contracts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    schema_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    table_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    cron_expression = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    owner_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    alert_on_failure = table.Column<bool>(type: "boolean", nullable: false),
                    failure_threshold_score = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_contracts", x => x.id);
                    table.ForeignKey(
                        name: "fk_data_contracts_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "data_quality_scores",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    schema_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    table_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    evaluated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    trend_direction = table.Column<int>(type: "integer", nullable: false),
                    previous_score = table.Column<double>(type: "double precision", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_quality_scores", x => x.id);
                    table.ForeignKey(
                        name: "fk_data_quality_scores_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "database_metadata",
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
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "manual_query_execution_logs",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    query_text = table.Column<string>(type: "text", nullable: false),
                    result_count = table.Column<int>(type: "integer", nullable: false),
                    execution_time_ms = table.Column<double>(type: "double precision", nullable: false),
                    data_source_id = table.Column<int>(type: "integer", nullable: true),
                    execution_context = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manual_query_execution_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_manual_query_execution_logs_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "migration_jobs",
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
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_migration_jobs_data_sources_destination_data_source_id",
                        column: x => x.destination_data_source_id,
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "git_hub_repositories",
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
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_data_sources",
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
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_data_sources_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_documentations",
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
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_key_credentials",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    key_prefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scopes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    allowed_project_ids = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    role_id = table.Column<int>(type: "integer", nullable: false),
                    assigned_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    assigned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_roles", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_actor_plans",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ai_actor_id = table.Column<int>(type: "integer", nullable: false),
                    ai_actor_execution_id = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    user_instruction = table.Column<string>(type: "text", nullable: true),
                    analysis = table.Column<string>(type: "text", nullable: false),
                    findings_json = table.Column<string>(type: "text", nullable: true),
                    actions_json = table.Column<string>(type: "text", nullable: false),
                    proposed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reviewed_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reviewer_comment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    executed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tokens_used = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric", nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    parent_plan_id = table.Column<int>(type: "integer", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_actor_plans", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_actor_plans_ai_actor_plans_parent_plan_id",
                        column: x => x.parent_plan_id,
                        principalTable: "ai_actor_plans",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_ai_actor_plans_ai_actors_ai_actor_id",
                        column: x => x.ai_actor_id,
                        principalTable: "ai_actors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "data_contract_recipient",
                columns: table => new
                {
                    data_contracts_id = table.Column<int>(type: "integer", nullable: false),
                    recipients_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_contract_recipient", x => new { x.data_contracts_id, x.recipients_id });
                    table.ForeignKey(
                        name: "fk_data_contract_recipient_data_contracts_data_contracts_id",
                        column: x => x.data_contracts_id,
                        principalTable: "data_contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_data_contract_recipient_recipients_recipients_id",
                        column: x => x.recipients_id,
                        principalTable: "recipients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "data_contract_rules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_contract_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    rule_type = table.Column<int>(type: "integer", nullable: false),
                    column_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    configuration = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<int>(type: "integer", nullable: false),
                    weight = table.Column<double>(type: "double precision", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_contract_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_data_contract_rules_data_contracts_data_contract_id",
                        column: x => x.data_contract_id,
                        principalTable: "data_contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "data_quality_evaluations",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_contract_id = table.Column<int>(type: "integer", nullable: false),
                    overall_score = table.Column<double>(type: "double precision", nullable: false),
                    passed_rules = table.Column<int>(type: "integer", nullable: false),
                    failed_rules = table.Column<int>(type: "integer", nullable: false),
                    total_rules = table.Column<int>(type: "integer", nullable: false),
                    execution_time_ms = table.Column<double>(type: "double precision", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_quality_evaluations", x => x.id);
                    table.ForeignKey(
                        name: "fk_data_quality_evaluations_data_contracts_data_contract_id",
                        column: x => x.data_contract_id,
                        principalTable: "data_contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "column_metadata",
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
                        principalTable: "database_metadata",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "index_metadata",
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
                        principalTable: "database_metadata",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_executions",
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
                        principalTable: "migration_executions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_migration_executions_migration_jobs_migration_job_id",
                        column: x => x.migration_job_id,
                        principalTable: "migration_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_references",
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
                        principalTable: "git_hub_repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_documentation_sections",
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
                        principalTable: "project_documentations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mcp_sessions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    api_key_id = table.Column<int>(type: "integer", nullable: true),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    session_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_activity_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
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
                        principalTable: "api_key_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_mcp_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "data_quality_rule_results",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_quality_evaluation_id = table.Column<int>(type: "integer", nullable: false),
                    data_contract_rule_id = table.Column<int>(type: "integer", nullable: false),
                    passed = table.Column<bool>(type: "boolean", nullable: false),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    actual_value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    expected_value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    execution_time_ms = table.Column<double>(type: "double precision", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_quality_rule_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_data_quality_rule_results_data_contract_rules_data_contract",
                        column: x => x.data_contract_rule_id,
                        principalTable: "data_contract_rules",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_data_quality_rule_results_data_quality_evaluations_data_qua",
                        column: x => x.data_quality_evaluation_id,
                        principalTable: "data_quality_evaluations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mcp_audit_logs",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<int>(type: "integer", nullable: true),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    tool = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    parameters = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    data_source_id = table.Column<int>(type: "integer", nullable: true),
                    project_id = table.Column<int>(type: "integer", nullable: true),
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
                        principalTable: "mcp_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_mcp_audit_logs_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ai_actor_conversations",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ai_actor_id = table.Column<int>(type: "integer", nullable: false),
                    ai_actor_execution_id = table.Column<int>(type: "integer", nullable: true),
                    turn_number = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    message_content = table.Column<string>(type: "text", nullable: false),
                    tokens_used = table.Column<int>(type: "integer", nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    metadata = table.Column<string>(type: "text", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_actor_conversations", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_actor_conversations_ai_actors_ai_actor_id",
                        column: x => x.ai_actor_id,
                        principalTable: "ai_actors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_actor_executions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ai_actor_id = table.Column<int>(type: "integer", nullable: false),
                    triggering_subscription_id = table.Column<int>(type: "integer", nullable: true),
                    phase = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    queries_analyzed = table.Column<int>(type: "integer", nullable: false),
                    queries_created = table.Column<int>(type: "integer", nullable: false),
                    queries_refined = table.Column<int>(type: "integer", nullable: false),
                    subscriptions_created = table.Column<int>(type: "integer", nullable: false),
                    notifications_triggered = table.Column<int>(type: "integer", nullable: false),
                    tokens_used = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric", nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    decision_summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    actions_json = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    detailed_analysis = table.Column<string>(type: "text", nullable: true),
                    findings_json = table.Column<string>(type: "text", nullable: true),
                    ai_actor_plan_id = table.Column<int>(type: "integer", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_actor_executions", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_actor_executions_ai_actor_plans_ai_actor_plan_id",
                        column: x => x.ai_actor_plan_id,
                        principalTable: "ai_actor_plans",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_ai_actor_executions_ai_actors_ai_actor_id",
                        column: x => x.ai_actor_id,
                        principalTable: "ai_actors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_alert_configurations",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    natural_language_description = table.Column<string>(type: "text", nullable: false),
                    generated_sql = table.Column<string>(type: "text", nullable: false),
                    final_sql = table.Column<string>(type: "text", nullable: true),
                    generated_by_model = table.Column<string>(type: "text", nullable: false),
                    generation_reasoning = table.Column<string>(type: "text", nullable: true),
                    confidence_score = table.Column<decimal>(type: "numeric", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    validation_errors = table.Column<string>(type: "text", nullable: true),
                    user_feedback = table.Column<string>(type: "text", nullable: true),
                    subscription_id = table.Column<int>(type: "integer", nullable: true),
                    conversation_turns = table.Column<int>(type: "integer", nullable: false),
                    tokens_used = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    modified_by = table.Column<string>(type: "text", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_alert_configurations", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_alert_configurations_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_conversation_histories",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ai_alert_configuration_id = table.Column<int>(type: "integer", nullable: false),
                    turn_number = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    message_content = table.Column<string>(type: "text", nullable: false),
                    tokens_used = table.Column<int>(type: "integer", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    metadata = table.Column<string>(type: "text", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_conversation_histories", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_conversation_histories_ai_alert_configurations_ai_alert_",
                        column: x => x.ai_alert_configuration_id,
                        principalTable: "ai_alert_configurations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_usage_metrics",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    data_source_id = table.Column<int>(type: "integer", nullable: true),
                    query_id = table.Column<int>(type: "integer", nullable: true),
                    documentation_id = table.Column<int>(type: "integer", nullable: true),
                    provider = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    operation_type = table.Column<int>(type: "integer", nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    total_tokens = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric", nullable: false),
                    prompt_cache_hit = table.Column<bool>(type: "boolean", nullable: false),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_usage_metrics", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_usage_metrics_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalTable: "data_sources",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "anomaly_baselines",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    subscription_id = table.Column<int>(type: "integer", nullable: false),
                    execution_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    metric_value = table.Column<decimal>(type: "numeric", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_anomaly_baselines", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "anomaly_configs",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    subscription_id = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    detection_method = table.Column<int>(type: "integer", nullable: false),
                    sensitivity = table.Column<int>(type: "integer", nullable: false),
                    lookback_days = table.Column<int>(type: "integer", nullable: false),
                    alert_on_increase = table.Column<bool>(type: "boolean", nullable: false),
                    alert_on_decrease = table.Column<bool>(type: "boolean", nullable: false),
                    minimum_data_points = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_anomaly_configs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "anomaly_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    subscription_id = table.Column<int>(type: "integer", nullable: false),
                    notification_id = table.Column<int>(type: "integer", nullable: true),
                    detected_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    current_value = table.Column<decimal>(type: "numeric", nullable: false),
                    baseline_mean = table.Column<decimal>(type: "numeric", nullable: true),
                    baseline_std_dev = table.Column<decimal>(type: "numeric", nullable: true),
                    z_score = table.Column<decimal>(type: "numeric", nullable: true),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    explanation = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    acknowledged_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_anomaly_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    query_execution_history_id = table.Column<int>(type: "integer", nullable: false),
                    recipient_id = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    results = table.Column<string>(type: "text", nullable: true),
                    task_id = table.Column<int>(type: "integer", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_recipients_recipient_id",
                        column: x => x.recipient_id,
                        principalTable: "recipients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "queries",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    folder_id = table.Column<int>(type: "integer", nullable: true),
                    final_query = table.Column<string>(type: "text", nullable: true),
                    ai_actor_id = table.Column<int>(type: "integer", nullable: true),
                    is_locked = table.Column<bool>(type: "boolean", nullable: false),
                    locked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    locked_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    active_version_id = table.Column<int>(type: "integer", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_queries", x => x.id);
                    table.ForeignKey(
                        name: "fk_queries_ai_actors_ai_actor_id",
                        column: x => x.ai_actor_id,
                        principalTable: "ai_actors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_queries_query_folders_folder_id",
                        column: x => x.folder_id,
                        principalTable: "query_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "query_parameters",
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
                        principalTable: "queries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "query_steps",
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
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_query_steps_queries_query_id",
                        column: x => x.query_id,
                        principalTable: "queries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "query_versions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    query_id = table.Column<int>(type: "integer", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    final_query = table.Column<string>(type: "text", nullable: true),
                    steps_json = table.Column<string>(type: "text", nullable: false),
                    created_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    change_source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    change_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_query_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_query_versions_queries_query_id",
                        column: x => x.query_id,
                        principalTable: "queries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    query_id = table.Column<int>(type: "integer", nullable: false),
                    cron_expression = table.Column<string>(type: "text", nullable: false),
                    max_rows = table.Column<int>(type: "integer", nullable: true),
                    minimum_row_count = table.Column<int>(type: "integer", nullable: true),
                    include_attachment = table.Column<bool>(type: "boolean", nullable: false),
                    result_attachment_type = table.Column<int>(type: "integer", nullable: true),
                    show_query = table.Column<bool>(type: "boolean", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: true),
                    store_results = table.Column<bool>(type: "boolean", nullable: false),
                    create_tasks = table.Column<bool>(type: "boolean", nullable: false),
                    notification_trigger = table.Column<int>(type: "integer", nullable: false),
                    ai_actor_id = table.Column<int>(type: "integer", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscriptions_ai_actors_ai_actor_id",
                        column: x => x.ai_actor_id,
                        principalTable: "ai_actors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_subscriptions_queries_query_id",
                        column: x => x.query_id,
                        principalTable: "queries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "query_step_change_history",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    query_step_id = table.Column<int>(type: "integer", nullable: false),
                    ai_actor_id = table.Column<int>(type: "integer", nullable: true),
                    ai_actor_execution_id = table.Column<int>(type: "integer", nullable: true),
                    ai_actor_plan_id = table.Column<int>(type: "integer", nullable: true),
                    user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    previous_sql = table.Column<string>(type: "text", nullable: false),
                    new_sql = table.Column<string>(type: "text", nullable: false),
                    change_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    change_source = table.Column<int>(type: "integer", nullable: false),
                    changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_query_step_change_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_query_step_change_history_ai_actor_executions_ai_actor_exec",
                        column: x => x.ai_actor_execution_id,
                        principalTable: "ai_actor_executions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_query_step_change_history_ai_actor_plans_ai_actor_plan_id",
                        column: x => x.ai_actor_plan_id,
                        principalTable: "ai_actor_plans",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_query_step_change_history_ai_actors_ai_actor_id",
                        column: x => x.ai_actor_id,
                        principalTable: "ai_actors",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_query_step_change_history_query_steps_query_step_id",
                        column: x => x.query_step_id,
                        principalTable: "query_steps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "query_step_parameters",
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
                        principalTable: "query_steps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "query_approval_requests",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    query_id = table.Column<int>(type: "integer", nullable: false),
                    query_version_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    requested_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    requested_by_user_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reviewed_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reviewed_by_user_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    review_comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    change_summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_query_approval_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_query_approval_requests_queries_query_id",
                        column: x => x.query_id,
                        principalTable: "queries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_query_approval_requests_query_versions_query_version_id",
                        column: x => x.query_version_id,
                        principalTable: "query_versions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "query_execution_history",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    subscription_id = table.Column<int>(type: "integer", nullable: false),
                    result_count = table.Column<int>(type: "integer", nullable: false),
                    compiled_sql = table.Column<string>(type: "text", nullable: false),
                    notification_status = table.Column<int>(type: "integer", nullable: false),
                    execution_time_ms = table.Column<double>(type: "double precision", nullable: false),
                    results = table.Column<string>(type: "text", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_query_execution_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_query_execution_history_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "query_tasks",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    subscription_id = table.Column<int>(type: "integer", nullable: false),
                    latest_result_count = table.Column<int>(type: "integer", nullable: false),
                    last_notification_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolved = table.Column<bool>(type: "boolean", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolved_by_user_id = table.Column<string>(type: "text", nullable: true),
                    resolution_notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_query_tasks", x => x.id);
                    table.ForeignKey(
                        name: "fk_query_tasks_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recipient_subscription",
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
                        principalTable: "recipients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_recipient_subscription_subscriptions_subscriptions_id",
                        column: x => x.subscriptions_id,
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subscription_parameters",
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
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_conversations_ai_actor_execution_id",
                table: "ai_actor_conversations",
                column: "ai_actor_execution_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_conversations_ai_actor_id_turn_number",
                table: "ai_actor_conversations",
                columns: new[] { "ai_actor_id", "turn_number" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_conversations_timestamp",
                table: "ai_actor_conversations",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_conversations_turn_number",
                table: "ai_actor_conversations",
                column: "turn_number");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_executions_ai_actor_id_started_at",
                table: "ai_actor_executions",
                columns: new[] { "ai_actor_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_executions_ai_actor_plan_id",
                table: "ai_actor_executions",
                column: "ai_actor_plan_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_executions_phase",
                table: "ai_actor_executions",
                column: "phase");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_executions_started_at",
                table: "ai_actor_executions",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_executions_triggering_subscription_id",
                table: "ai_actor_executions",
                column: "triggering_subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_plans_ai_actor_id_proposed_at",
                table: "ai_actor_plans",
                columns: new[] { "ai_actor_id", "proposed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_plans_ai_actor_id_status",
                table: "ai_actor_plans",
                columns: new[] { "ai_actor_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_plans_parent_plan_id",
                table: "ai_actor_plans",
                column: "parent_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_plans_proposed_at",
                table: "ai_actor_plans",
                column: "proposed_at");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_plans_status",
                table: "ai_actor_plans",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actors_archived_time",
                table: "ai_actors",
                column: "archived_time");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actors_data_source_id_status",
                table: "ai_actors",
                columns: new[] { "data_source_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_actors_status",
                table: "ai_actors",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actors_status_archived_time",
                table: "ai_actors",
                columns: new[] { "status", "archived_time" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_alert_configurations_data_source_id",
                table: "ai_alert_configurations",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_alert_configurations_status",
                table: "ai_alert_configurations",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_ai_alert_configurations_subscription_id",
                table: "ai_alert_configurations",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_conversation_histories_ai_alert_configuration_id",
                table: "ai_conversation_histories",
                column: "ai_alert_configuration_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_conversation_histories_timestamp",
                table: "ai_conversation_histories",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_ai_conversation_histories_turn_number",
                table: "ai_conversation_histories",
                column: "turn_number");

            migrationBuilder.CreateIndex(
                name: "ix_ai_prompt_templates_is_active",
                table: "ai_prompt_templates",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_ai_prompt_templates_operation_type",
                table: "ai_prompt_templates",
                column: "operation_type");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_data_source_id",
                table: "ai_usage_metrics",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_operation_type",
                table: "ai_usage_metrics",
                column: "operation_type");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_provider",
                table: "ai_usage_metrics",
                column: "provider");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_query_id",
                table: "ai_usage_metrics",
                column: "query_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_timestamp",
                table: "ai_usage_metrics",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_metrics_user_id",
                table: "ai_usage_metrics",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_baselines_execution_time",
                table: "anomaly_baselines",
                column: "execution_time");

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_baselines_subscription_id_execution_time",
                table: "anomaly_baselines",
                columns: new[] { "subscription_id", "execution_time" });

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_configs_enabled",
                table: "anomaly_configs",
                column: "enabled");

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_configs_subscription_id",
                table: "anomaly_configs",
                column: "subscription_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_events_acknowledged_detected_time",
                table: "anomaly_events",
                columns: new[] { "acknowledged", "detected_time" });

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_events_detected_time",
                table: "anomaly_events",
                column: "detected_time");

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_events_notification_id",
                table: "anomaly_events",
                column: "notification_id");

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_events_subscription_id_detected_time",
                table: "anomaly_events",
                columns: new[] { "subscription_id", "detected_time" });

            migrationBuilder.CreateIndex(
                name: "ix_api_key_credentials_is_revoked",
                table: "api_key_credentials",
                column: "is_revoked");

            migrationBuilder.CreateIndex(
                name: "ix_api_key_credentials_key_hash",
                table: "api_key_credentials",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_key_credentials_key_prefix",
                table: "api_key_credentials",
                column: "key_prefix");

            migrationBuilder.CreateIndex(
                name: "ix_api_key_credentials_user_id",
                table: "api_key_credentials",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_app_setting_history_changed_at",
                table: "app_setting_history",
                column: "changed_at");

            migrationBuilder.CreateIndex(
                name: "ix_app_setting_history_setting_key",
                table: "app_setting_history",
                column: "setting_key");

            migrationBuilder.CreateIndex(
                name: "ix_app_settings_category",
                table: "app_settings",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_app_settings_key",
                table: "app_settings",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_references_git_hub_repository_id",
                table: "code_references",
                column: "git_hub_repository_id");

            migrationBuilder.CreateIndex(
                name: "ix_code_references_reference_type",
                table: "code_references",
                column: "reference_type");

            migrationBuilder.CreateIndex(
                name: "ix_code_references_schema_name_table_name",
                table: "code_references",
                columns: new[] { "schema_name", "table_name" });

            migrationBuilder.CreateIndex(
                name: "ix_column_metadata_database_metadata_id",
                table: "column_metadata",
                column: "database_metadata_id");

            migrationBuilder.CreateIndex(
                name: "ix_column_metadata_database_metadata_id_column_name",
                table: "column_metadata",
                columns: new[] { "database_metadata_id", "column_name" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_created_time",
                table: "comments",
                column: "created_time");

            migrationBuilder.CreateIndex(
                name: "ix_comments_entity_type_entity_id",
                table: "comments",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_permissions_dashboard_id",
                table: "dashboard_permissions",
                column: "dashboard_id");

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_permissions_dashboard_id_user_id",
                table: "dashboard_permissions",
                columns: new[] { "dashboard_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_permissions_user_id",
                table: "dashboard_permissions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_widgets_dashboard_id",
                table: "dashboard_widgets",
                column: "dashboard_id");

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_widgets_dashboard_id_sort_order",
                table: "dashboard_widgets",
                columns: new[] { "dashboard_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_widgets_widget_type",
                table: "dashboard_widgets",
                column: "widget_type");

            migrationBuilder.CreateIndex(
                name: "ix_dashboards_archived_time",
                table: "dashboards",
                column: "archived_time");

            migrationBuilder.CreateIndex(
                name: "ix_dashboards_created_by_user_id",
                table: "dashboards",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_dashboards_is_default",
                table: "dashboards",
                column: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_dashboards_is_shared",
                table: "dashboards",
                column: "is_shared");

            migrationBuilder.CreateIndex(
                name: "ix_data_contract_recipient_recipients_id",
                table: "data_contract_recipient",
                column: "recipients_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_contract_rules_data_contract_id",
                table: "data_contract_rules",
                column: "data_contract_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_contracts_archived_time",
                table: "data_contracts",
                column: "archived_time");

            migrationBuilder.CreateIndex(
                name: "ix_data_contracts_data_source_id",
                table: "data_contracts",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_contracts_data_source_id_schema_name_table_name",
                table: "data_contracts",
                columns: new[] { "data_source_id", "schema_name", "table_name" });

            migrationBuilder.CreateIndex(
                name: "ix_data_contracts_is_enabled",
                table: "data_contracts",
                column: "is_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_data_quality_evaluations_created_time",
                table: "data_quality_evaluations",
                column: "created_time");

            migrationBuilder.CreateIndex(
                name: "ix_data_quality_evaluations_data_contract_id",
                table: "data_quality_evaluations",
                column: "data_contract_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_quality_rule_results_data_contract_rule_id",
                table: "data_quality_rule_results",
                column: "data_contract_rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_quality_rule_results_data_quality_evaluation_id",
                table: "data_quality_rule_results",
                column: "data_quality_evaluation_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_quality_scores_data_source_id_schema_name_table_name",
                table: "data_quality_scores",
                columns: new[] { "data_source_id", "schema_name", "table_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_data_quality_scores_evaluated_at",
                table: "data_quality_scores",
                column: "evaluated_at");

            migrationBuilder.CreateIndex(
                name: "ix_database_metadata_data_source_id",
                table: "database_metadata",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_database_metadata_data_source_id_schema_name_table_name",
                table: "database_metadata",
                columns: new[] { "data_source_id", "schema_name", "table_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_database_metadata_last_refreshed",
                table: "database_metadata",
                column: "last_refreshed");

            migrationBuilder.CreateIndex(
                name: "ix_git_hub_repositories_project_id",
                table: "git_hub_repositories",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_git_hub_repositories_scan_status",
                table: "git_hub_repositories",
                column: "scan_status");

            migrationBuilder.CreateIndex(
                name: "ix_index_metadata_database_metadata_id",
                table: "index_metadata",
                column: "database_metadata_id");

            migrationBuilder.CreateIndex(
                name: "ix_manual_query_execution_logs_created_time",
                table: "manual_query_execution_logs",
                column: "created_time");

            migrationBuilder.CreateIndex(
                name: "ix_manual_query_execution_logs_data_source_id",
                table: "manual_query_execution_logs",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_manual_query_execution_logs_data_source_id_created_time",
                table: "manual_query_execution_logs",
                columns: new[] { "data_source_id", "created_time" });

            migrationBuilder.CreateIndex(
                name: "ix_manual_query_execution_logs_execution_context",
                table: "manual_query_execution_logs",
                column: "execution_context");

            migrationBuilder.CreateIndex(
                name: "ix_manual_query_execution_logs_user_id",
                table: "manual_query_execution_logs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_manual_query_execution_logs_user_id_created_time",
                table: "manual_query_execution_logs",
                columns: new[] { "user_id", "created_time" });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_audit_logs_created_time",
                table: "mcp_audit_logs",
                column: "created_time");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_audit_logs_session_id",
                table: "mcp_audit_logs",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_audit_logs_tool",
                table: "mcp_audit_logs",
                column: "tool");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_audit_logs_user_id",
                table: "mcp_audit_logs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_documentation_patches_data_source_id",
                table: "mcp_documentation_patches",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_documentation_patches_project_id_status",
                table: "mcp_documentation_patches",
                columns: new[] { "project_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_learned_patterns_data_source_id_status_table_name",
                table: "mcp_learned_patterns",
                columns: new[] { "data_source_id", "status", "table_name" });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_learned_patterns_project_id",
                table: "mcp_learned_patterns",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_query_signals_created_time",
                table: "mcp_query_signals",
                column: "created_time");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_query_signals_data_source_id",
                table: "mcp_query_signals",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_query_signals_is_successful",
                table: "mcp_query_signals",
                column: "is_successful");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_query_signals_project_id",
                table: "mcp_query_signals",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_sessions_api_key_id",
                table: "mcp_sessions",
                column: "api_key_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_sessions_last_activity_at",
                table: "mcp_sessions",
                column: "last_activity_at");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_sessions_session_id",
                table: "mcp_sessions",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mcp_sessions_user_id",
                table: "mcp_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_migration_executions_migration_job_id",
                table: "migration_executions",
                column: "migration_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_migration_executions_parent_execution_id",
                table: "migration_executions",
                column: "parent_execution_id");

            migrationBuilder.CreateIndex(
                name: "ix_migration_executions_started_at",
                table: "migration_executions",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_migration_executions_status_started_at",
                table: "migration_executions",
                columns: new[] { "status", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_jobs_data_source_id",
                table: "migration_jobs",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_migration_jobs_destination_data_source_id",
                table: "migration_jobs",
                column: "destination_data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_migration_jobs_is_enabled_archived_time",
                table: "migration_jobs",
                columns: new[] { "is_enabled", "archived_time" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_query_execution_history_id",
                table: "notifications",
                column: "query_execution_history_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_id",
                table: "notifications",
                column: "recipient_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_task_id",
                table: "notifications",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_data_sources_data_source_id",
                table: "project_data_sources",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_data_sources_project_id_data_source_id",
                table: "project_data_sources",
                columns: new[] { "project_id", "data_source_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_documentation_sections_project_documentation_id",
                table: "project_documentation_sections",
                column: "project_documentation_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_documentation_sections_section_type",
                table: "project_documentation_sections",
                column: "section_type");

            migrationBuilder.CreateIndex(
                name: "ix_project_documentations_generated_at",
                table: "project_documentations",
                column: "generated_at");

            migrationBuilder.CreateIndex(
                name: "ix_project_documentations_project_id",
                table: "project_documentations",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_projects_name",
                table: "projects",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_queries_active_version_id",
                table: "queries",
                column: "active_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_queries_ai_actor_id",
                table: "queries",
                column: "ai_actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_queries_folder_id",
                table: "queries",
                column: "folder_id");

            migrationBuilder.CreateIndex(
                name: "ix_queries_is_locked",
                table: "queries",
                column: "is_locked");

            migrationBuilder.CreateIndex(
                name: "ix_query_approval_requests_query_id_status",
                table: "query_approval_requests",
                columns: new[] { "query_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_query_approval_requests_query_version_id",
                table: "query_approval_requests",
                column: "query_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_approval_requests_status",
                table: "query_approval_requests",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_query_approval_requests_status_created_time",
                table: "query_approval_requests",
                columns: new[] { "status", "created_time" });

            migrationBuilder.CreateIndex(
                name: "ix_query_execution_history_subscription_id",
                table: "query_execution_history",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_folders_archived_time",
                table: "query_folders",
                column: "archived_time");

            migrationBuilder.CreateIndex(
                name: "ix_query_folders_parent_folder_id",
                table: "query_folders",
                column: "parent_folder_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_folders_parent_folder_id_name",
                table: "query_folders",
                columns: new[] { "parent_folder_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_query_folders_parent_folder_id_sort_order",
                table: "query_folders",
                columns: new[] { "parent_folder_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_query_folders_path",
                table: "query_folders",
                column: "path");

            migrationBuilder.CreateIndex(
                name: "ix_query_parameters_query_id",
                table: "query_parameters",
                column: "query_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_step_change_history_ai_actor_execution_id",
                table: "query_step_change_history",
                column: "ai_actor_execution_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_step_change_history_ai_actor_id",
                table: "query_step_change_history",
                column: "ai_actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_step_change_history_ai_actor_plan_id",
                table: "query_step_change_history",
                column: "ai_actor_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_step_change_history_change_source",
                table: "query_step_change_history",
                column: "change_source");

            migrationBuilder.CreateIndex(
                name: "ix_query_step_change_history_changed_at",
                table: "query_step_change_history",
                column: "changed_at");

            migrationBuilder.CreateIndex(
                name: "ix_query_step_change_history_query_step_id_changed_at",
                table: "query_step_change_history",
                columns: new[] { "query_step_id", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_query_step_parameters_query_step_id",
                table: "query_step_parameters",
                column: "query_step_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_steps_data_source_id",
                table: "query_steps",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_steps_query_id",
                table: "query_steps",
                column: "query_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_tasks_created_time",
                table: "query_tasks",
                column: "created_time");

            migrationBuilder.CreateIndex(
                name: "ix_query_tasks_resolved_created_time",
                table: "query_tasks",
                columns: new[] { "resolved", "created_time" });

            migrationBuilder.CreateIndex(
                name: "ix_query_tasks_subscription_id",
                table: "query_tasks",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_versions_query_id_status",
                table: "query_versions",
                columns: new[] { "query_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_query_versions_query_id_version_number",
                table: "query_versions",
                columns: new[] { "query_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recipient_subscription_subscriptions_id",
                table: "recipient_subscription",
                column: "subscriptions_id");

            migrationBuilder.CreateIndex(
                name: "ix_roles_name",
                table: "roles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscription_parameters_subscription_id",
                table: "subscription_parameters",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_ai_actor_id",
                table: "subscriptions",
                column: "ai_actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_query_id",
                table: "subscriptions",
                column: "query_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_assigned_at",
                table: "user_roles",
                column: "assigned_at");

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_role_id",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_user_id",
                table: "user_roles",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_user_id_role_id",
                table: "user_roles",
                columns: new[] { "user_id", "role_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_archived_time",
                table: "users",
                column: "archived_time");

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "ix_users_external_id",
                table: "users",
                column: "external_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_identity_provider_external_id",
                table: "users",
                columns: new[] { "identity_provider", "external_id" });

            migrationBuilder.CreateIndex(
                name: "ix_users_is_enabled",
                table: "users",
                column: "is_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_users_is_internal_user",
                table: "users",
                column: "is_internal_user");

            migrationBuilder.CreateIndex(
                name: "ix_users_is_super_admin",
                table: "users",
                column: "is_super_admin");

            migrationBuilder.CreateIndex(
                name: "ix_users_user_name_archived_time",
                table: "users",
                columns: new[] { "user_name", "archived_time" });

            migrationBuilder.AddForeignKey(
                name: "fk_ai_actor_conversations_ai_actor_executions_ai_actor_executi",
                table: "ai_actor_conversations",
                column: "ai_actor_execution_id",
                principalTable: "ai_actor_executions",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_ai_actor_executions_subscriptions_triggering_subscription_id",
                table: "ai_actor_executions",
                column: "triggering_subscription_id",
                principalTable: "subscriptions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_ai_alert_configurations_subscriptions_subscription_id",
                table: "ai_alert_configurations",
                column: "subscription_id",
                principalTable: "subscriptions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_ai_usage_metrics_queries_query_id",
                table: "ai_usage_metrics",
                column: "query_id",
                principalTable: "queries",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_anomaly_baselines_subscriptions_subscription_id",
                table: "anomaly_baselines",
                column: "subscription_id",
                principalTable: "subscriptions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_anomaly_configs_subscriptions_subscription_id",
                table: "anomaly_configs",
                column: "subscription_id",
                principalTable: "subscriptions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_anomaly_events_notifications_notification_id",
                table: "anomaly_events",
                column: "notification_id",
                principalTable: "notifications",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_anomaly_events_subscriptions_subscription_id",
                table: "anomaly_events",
                column: "subscription_id",
                principalTable: "subscriptions",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_notifications_query_execution_history_query_execution_histo",
                table: "notifications",
                column: "query_execution_history_id",
                principalTable: "query_execution_history",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_notifications_query_tasks_task_id",
                table: "notifications",
                column: "task_id",
                principalTable: "query_tasks",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_queries_query_versions_active_version_id",
                table: "queries",
                column: "active_version_id",
                principalTable: "query_versions",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_queries_ai_actors_ai_actor_id",
                table: "queries");

            migrationBuilder.DropForeignKey(
                name: "fk_query_versions_queries_query_id",
                table: "query_versions");

            migrationBuilder.DropTable(
                name: "ai_actor_conversations");

            migrationBuilder.DropTable(
                name: "ai_conversation_histories");

            migrationBuilder.DropTable(
                name: "ai_prompt_templates");

            migrationBuilder.DropTable(
                name: "ai_usage_metrics");

            migrationBuilder.DropTable(
                name: "anomaly_baselines");

            migrationBuilder.DropTable(
                name: "anomaly_configs");

            migrationBuilder.DropTable(
                name: "anomaly_events");

            migrationBuilder.DropTable(
                name: "app_setting_history");

            migrationBuilder.DropTable(
                name: "app_settings");

            migrationBuilder.DropTable(
                name: "code_references");

            migrationBuilder.DropTable(
                name: "column_metadata");

            migrationBuilder.DropTable(
                name: "comments");

            migrationBuilder.DropTable(
                name: "dashboard_permissions");

            migrationBuilder.DropTable(
                name: "dashboard_widgets");

            migrationBuilder.DropTable(
                name: "data_contract_recipient");

            migrationBuilder.DropTable(
                name: "data_protection_keys");

            migrationBuilder.DropTable(
                name: "data_quality_rule_results");

            migrationBuilder.DropTable(
                name: "data_quality_scores");

            migrationBuilder.DropTable(
                name: "index_metadata");

            migrationBuilder.DropTable(
                name: "manual_query_execution_logs");

            migrationBuilder.DropTable(
                name: "mcp_audit_logs");

            migrationBuilder.DropTable(
                name: "mcp_documentation_patches");

            migrationBuilder.DropTable(
                name: "mcp_learned_patterns");

            migrationBuilder.DropTable(
                name: "mcp_query_signals");

            migrationBuilder.DropTable(
                name: "mcp_settings");

            migrationBuilder.DropTable(
                name: "migration_executions");

            migrationBuilder.DropTable(
                name: "project_data_sources");

            migrationBuilder.DropTable(
                name: "project_documentation_sections");

            migrationBuilder.DropTable(
                name: "query_approval_requests");

            migrationBuilder.DropTable(
                name: "query_parameters");

            migrationBuilder.DropTable(
                name: "query_step_change_history");

            migrationBuilder.DropTable(
                name: "query_step_parameters");

            migrationBuilder.DropTable(
                name: "recipient_subscription");

            migrationBuilder.DropTable(
                name: "subscription_parameters");

            migrationBuilder.DropTable(
                name: "user_roles");

            migrationBuilder.DropTable(
                name: "ai_alert_configurations");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "git_hub_repositories");

            migrationBuilder.DropTable(
                name: "dashboards");

            migrationBuilder.DropTable(
                name: "data_contract_rules");

            migrationBuilder.DropTable(
                name: "data_quality_evaluations");

            migrationBuilder.DropTable(
                name: "database_metadata");

            migrationBuilder.DropTable(
                name: "mcp_sessions");

            migrationBuilder.DropTable(
                name: "migration_jobs");

            migrationBuilder.DropTable(
                name: "project_documentations");

            migrationBuilder.DropTable(
                name: "ai_actor_executions");

            migrationBuilder.DropTable(
                name: "query_steps");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "query_execution_history");

            migrationBuilder.DropTable(
                name: "query_tasks");

            migrationBuilder.DropTable(
                name: "recipients");

            migrationBuilder.DropTable(
                name: "data_contracts");

            migrationBuilder.DropTable(
                name: "api_key_credentials");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "ai_actor_plans");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "ai_actors");

            migrationBuilder.DropTable(
                name: "data_sources");

            migrationBuilder.DropTable(
                name: "queries");

            migrationBuilder.DropTable(
                name: "query_folders");

            migrationBuilder.DropTable(
                name: "query_versions");
        }
    }
}
