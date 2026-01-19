using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class DatabaseImprovements_ConsolidateTimestamps_Enums_Jsonb_Indexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ========================================
            // POINT 2: Consolidate Duplicate Timestamp Fields
            // ========================================
            // Remove duplicate timestamp fields (created_at, modified_at) in favor of created_time
            // BaseEntity already defines CreatedTime, so we'll keep that column name

            // ai_alert_configurations: Remove created_at, modified_at (keep created_time)
            migrationBuilder.Sql(@"
                UPDATE semantico.ai_alert_configurations
                SET created_time = created_at
                WHERE created_time IS NULL OR created_time != created_at;
            ");
            migrationBuilder.DropColumn(name: "created_at", schema: "semantico", table: "ai_alert_configurations");
            migrationBuilder.DropColumn(name: "modified_at", schema: "semantico", table: "ai_alert_configurations");

            // ai_prompt_templates: Remove created_at, modified_at (keep created_time)
            migrationBuilder.Sql(@"
                UPDATE semantico.ai_prompt_templates
                SET created_time = created_at
                WHERE created_time IS NULL OR created_time != created_at;
            ");
            migrationBuilder.DropColumn(name: "created_at", schema: "semantico", table: "ai_prompt_templates");
            migrationBuilder.DropColumn(name: "modified_at", schema: "semantico", table: "ai_prompt_templates");

            // data_source_documentations: Remove created_at, modified_at, last_modified_at (keep created_time)
            migrationBuilder.Sql(@"
                UPDATE semantico.data_source_documentations
                SET created_time = created_at
                WHERE created_time IS NULL OR created_time != created_at;
            ");
            migrationBuilder.DropColumn(name: "created_at", schema: "semantico", table: "data_source_documentations");
            migrationBuilder.DropColumn(name: "modified_at", schema: "semantico", table: "data_source_documentations");
            migrationBuilder.DropColumn(name: "last_modified_at", schema: "semantico", table: "data_source_documentations");

            // documentation_sections: Remove created_at, modified_at (keep created_time)
            migrationBuilder.Sql(@"
                UPDATE semantico.documentation_sections
                SET created_time = created_at
                WHERE created_time IS NULL OR created_time != created_at;
            ");
            migrationBuilder.DropColumn(name: "created_at", schema: "semantico", table: "documentation_sections");
            migrationBuilder.DropColumn(name: "modified_at", schema: "semantico", table: "documentation_sections");

            // documentation_versions: Remove created_at (keep created_time)
            migrationBuilder.Sql(@"
                UPDATE semantico.documentation_versions
                SET created_time = created_at
                WHERE created_time IS NULL OR created_time != created_at;
            ");
            migrationBuilder.DropColumn(name: "created_at", schema: "semantico", table: "documentation_versions");

            // ========================================
            // POINT 3: Add CHECK Constraints for Data Validation
            // ========================================

            // Subscription timeout must be positive
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.subscriptions
                ADD CONSTRAINT check_timeout_positive
                CHECK (timeout_seconds IS NULL OR timeout_seconds > 0);
            ");

            // Subscription max_rows must be positive
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.subscriptions
                ADD CONSTRAINT check_max_rows_positive
                CHECK (max_rows IS NULL OR max_rows > 0);
            ");

            // Documentation agent run progress must be 0-100
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.documentation_agent_runs
                ADD CONSTRAINT check_progress_range
                CHECK (progress_percent BETWEEN 0 AND 100);
            ");

            // Anomaly z-score reasonableness check
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.anomaly_events
                ADD CONSTRAINT check_zscore_reasonable
                CHECK (z_score IS NULL OR z_score BETWEEN -10 AND 10);
            ");

            // Anomaly baseline lookback days must be positive
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.anomaly_configs
                ADD CONSTRAINT check_lookback_days_positive
                CHECK (lookback_days > 0 AND lookback_days <= 365);
            ");

            // Anomaly config minimum data points must be positive
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.anomaly_configs
                ADD CONSTRAINT check_minimum_data_points_positive
                CHECK (minimum_data_points > 0 AND minimum_data_points <= 1000);
            ");

            // AI token usage must be non-negative
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.ai_usage_metrics
                ADD CONSTRAINT check_tokens_non_negative
                CHECK (input_tokens >= 0 AND output_tokens >= 0 AND total_tokens >= 0);
            ");

            // AI cost must be non-negative
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.ai_usage_metrics
                ADD CONSTRAINT check_cost_non_negative
                CHECK (estimated_cost >= 0);
            ");

            // Migration execution row counts must be non-negative
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.migration_execution_histories
                ADD CONSTRAINT check_row_counts_non_negative
                CHECK (
                    source_rows_read >= 0 AND
                    destination_rows_written >= 0 AND
                    rows_skipped >= 0 AND
                    rows_failed >= 0 AND
                    processed_rows >= 0 AND
                    (estimated_total_rows IS NULL OR estimated_total_rows >= 0)
                );
            ");

            // ========================================
            // POINT 4: Convert JSON-in-TEXT to JSONB
            // ========================================

            // documentation_agent_runs: Convert JSON text fields to JSONB
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.documentation_agent_runs
                ALTER COLUMN discovered_tables_json TYPE jsonb USING discovered_tables_json::jsonb;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE semantico.documentation_agent_runs
                ALTER COLUMN domain_groups_json TYPE jsonb USING domain_groups_json::jsonb;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE semantico.documentation_agent_runs
                ALTER COLUMN completed_tables_json TYPE jsonb USING completed_tables_json::jsonb;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE semantico.documentation_agent_runs
                ALTER COLUMN failed_tables_json TYPE jsonb USING failed_tables_json::jsonb;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE semantico.documentation_agent_runs
                ALTER COLUMN checkpoint_state_json TYPE jsonb USING checkpoint_state_json::jsonb;
            ");

            // ai_prompt_templates: Convert variable_definitions to JSONB
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.ai_prompt_templates
                ALTER COLUMN variable_definitions TYPE jsonb USING variable_definitions::jsonb;
            ");

            // ai_conversation_histories: Convert metadata to JSONB
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.ai_conversation_histories
                ALTER COLUMN metadata TYPE jsonb USING metadata::jsonb;
            ");

            // data_source_documentations: Convert metadata to JSONB
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.data_source_documentations
                ALTER COLUMN metadata TYPE jsonb USING metadata::jsonb;
            ");

            // ========================================
            // POINT 5: Define Foreign Key Cascade Behaviors Explicitly
            // ========================================

            // Subscriptions -> DataSource: RESTRICT (prevent deletion of data sources with active subscriptions)
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.subscriptions
                DROP CONSTRAINT IF EXISTS fk_subscriptions_data_sources_data_source_id;

                ALTER TABLE semantico.subscriptions
                ADD CONSTRAINT fk_subscriptions_data_sources_data_source_id
                FOREIGN KEY (data_source_id)
                REFERENCES semantico.data_sources(id)
                ON DELETE RESTRICT;
            ");

            // QueryExecutionHistory -> Subscription: CASCADE (delete history when subscription is deleted)
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.query_execution_history
                DROP CONSTRAINT IF EXISTS fk_query_execution_history_subscriptions_subscription_id;

                ALTER TABLE semantico.query_execution_history
                ADD CONSTRAINT fk_query_execution_history_subscriptions_subscription_id
                FOREIGN KEY (subscription_id)
                REFERENCES semantico.subscriptions(id)
                ON DELETE CASCADE;
            ");

            // Notifications -> Recipient: RESTRICT (prevent deletion of recipients with notification history)
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.notifications
                DROP CONSTRAINT IF EXISTS fk_notifications_recipients_recipient_id;

                ALTER TABLE semantico.notifications
                ADD CONSTRAINT fk_notifications_recipients_recipient_id
                FOREIGN KEY (recipient_id)
                REFERENCES semantico.recipients(id)
                ON DELETE RESTRICT;
            ");

            // AnomalyBaselines -> Subscription: CASCADE
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.anomaly_baselines
                DROP CONSTRAINT IF EXISTS fk_anomaly_baselines_subscriptions_subscription_id;

                ALTER TABLE semantico.anomaly_baselines
                ADD CONSTRAINT fk_anomaly_baselines_subscriptions_subscription_id
                FOREIGN KEY (subscription_id)
                REFERENCES semantico.subscriptions(id)
                ON DELETE CASCADE;
            ");

            // AnomalyConfigs -> Subscription: CASCADE
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.anomaly_configs
                DROP CONSTRAINT IF EXISTS fk_anomaly_configs_subscriptions_subscription_id;

                ALTER TABLE semantico.anomaly_configs
                ADD CONSTRAINT fk_anomaly_configs_subscriptions_subscription_id
                FOREIGN KEY (subscription_id)
                REFERENCES semantico.subscriptions(id)
                ON DELETE CASCADE;
            ");

            // DocumentationSections -> DataSourceDocumentation: CASCADE
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.documentation_sections
                DROP CONSTRAINT IF EXISTS fk_documentation_sections_data_source_documentations_documentat;

                ALTER TABLE semantico.documentation_sections
                ADD CONSTRAINT fk_documentation_sections_data_source_documentations_documentat
                FOREIGN KEY (documentation_id)
                REFERENCES semantico.data_source_documentations(id)
                ON DELETE CASCADE;
            ");

            // DocumentationVersions -> DataSourceDocumentation: CASCADE
            migrationBuilder.Sql(@"
                ALTER TABLE semantico.documentation_versions
                DROP CONSTRAINT IF EXISTS fk_documentation_versions_data_source_documentations_documenta;

                ALTER TABLE semantico.documentation_versions
                ADD CONSTRAINT fk_documentation_versions_data_source_documentations_documenta
                FOREIGN KEY (documentation_id)
                REFERENCES semantico.data_source_documentations(id)
                ON DELETE CASCADE;
            ");

            // ========================================
            // POINT 6: Add Missing Indexes
            // ========================================

            // High-traffic query patterns for subscriptions
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_subscriptions_active_cron
                ON semantico.subscriptions(cron_expression, archived_time)
                WHERE archived_time IS NULL;
            ");

            // Query execution history lookups (covering index)
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_qeh_subscription_created
                ON semantico.query_execution_history(subscription_id, created_time DESC)
                INCLUDE (result_count, execution_time_ms);
            ");

            // Anomaly detection queries (partial index for hot data)
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_anomaly_baselines_sub_time
                ON semantico.anomaly_baselines(subscription_id, execution_time DESC)
                WHERE execution_time >= NOW() - INTERVAL '90 days';
            ");

            // Notification delivery tracking
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_notifications_status_created
                ON semantico.notifications(notification_status, created_time DESC)
                WHERE notification_status != 2;
            ");

            // AI cost tracking
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_ai_usage_timestamp_provider
                ON semantico.ai_usage_metrics(timestamp, provider, model)
                INCLUDE (estimated_cost, total_tokens);
            ");

            // Recipient subscription lookups
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_recipient_subscription_subscriptions
                ON semantico.recipient_subscription(subscriptions_id, recipients_id);
            ");

            // Active queries filter
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_queries_active
                ON semantico.queries(id)
                WHERE archived_time IS NULL;
            ");

            // Active data sources filter
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_data_sources_active
                ON semantico.data_sources(id)
                WHERE archived_time IS NULL;
            ");

            // Active recipients filter
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_recipients_active
                ON semantico.recipients(id)
                WHERE archived_time IS NULL;
            ");

            // Documentation agent run status lookup
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_doc_agent_runs_data_source_status
                ON semantico.documentation_agent_runs(data_source_id, status, started_at DESC);
            ");

            // GIN indexes for JSONB columns (for efficient querying)
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_doc_agent_runs_discovered_tables_gin
                ON semantico.documentation_agent_runs USING GIN (discovered_tables_json);
            ");

            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_doc_agent_runs_domain_groups_gin
                ON semantico.documentation_agent_runs USING GIN (domain_groups_json);
            ");

            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_ai_prompt_templates_variables_gin
                ON semantico.ai_prompt_templates USING GIN (variable_definitions);
            ");

            // Anomaly events - unacknowledged events
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_anomaly_events_unacknowledged
                ON semantico.anomaly_events(subscription_id, detected_time DESC)
                WHERE acknowledged = false;
            ");

            // Query parameters by query_id
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_query_parameters_query_active
                ON semantico.query_parameters(query_id)
                WHERE archived_time IS NULL;
            ");

            // Subscription parameters by subscription_id
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_subscription_parameters_subscription_active
                ON semantico.subscription_parameters(subscription_id)
                WHERE archived_time IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ========================================
            // REVERSE POINT 2: Restore Duplicate Timestamp Fields
            // ========================================

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                schema: "semantico",
                table: "ai_alert_configurations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: DateTime.UtcNow);

            migrationBuilder.AddColumn<DateTime>(
                name: "modified_at",
                schema: "semantico",
                table: "ai_alert_configurations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: DateTime.UtcNow);

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                schema: "semantico",
                table: "ai_prompt_templates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: DateTime.UtcNow);

            migrationBuilder.AddColumn<DateTime>(
                name: "modified_at",
                schema: "semantico",
                table: "ai_prompt_templates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: DateTime.UtcNow);

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                schema: "semantico",
                table: "data_source_documentations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: DateTime.UtcNow);

            migrationBuilder.AddColumn<DateTime>(
                name: "modified_at",
                schema: "semantico",
                table: "data_source_documentations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: DateTime.UtcNow);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_modified_at",
                schema: "semantico",
                table: "data_source_documentations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                schema: "semantico",
                table: "documentation_sections",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: DateTime.UtcNow);

            migrationBuilder.AddColumn<DateTime>(
                name: "modified_at",
                schema: "semantico",
                table: "documentation_sections",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: DateTime.UtcNow);

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                schema: "semantico",
                table: "documentation_versions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: DateTime.UtcNow);

            // ========================================
            // REVERSE POINT 3: Remove CHECK Constraints
            // ========================================

            migrationBuilder.Sql("ALTER TABLE semantico.subscriptions DROP CONSTRAINT IF EXISTS check_timeout_positive;");
            migrationBuilder.Sql("ALTER TABLE semantico.subscriptions DROP CONSTRAINT IF EXISTS check_max_rows_positive;");
            migrationBuilder.Sql("ALTER TABLE semantico.documentation_agent_runs DROP CONSTRAINT IF EXISTS check_progress_range;");
            migrationBuilder.Sql("ALTER TABLE semantico.anomaly_events DROP CONSTRAINT IF EXISTS check_zscore_reasonable;");
            migrationBuilder.Sql("ALTER TABLE semantico.anomaly_configs DROP CONSTRAINT IF EXISTS check_lookback_days_positive;");
            migrationBuilder.Sql("ALTER TABLE semantico.anomaly_configs DROP CONSTRAINT IF EXISTS check_minimum_data_points_positive;");
            migrationBuilder.Sql("ALTER TABLE semantico.ai_usage_metrics DROP CONSTRAINT IF EXISTS check_tokens_non_negative;");
            migrationBuilder.Sql("ALTER TABLE semantico.ai_usage_metrics DROP CONSTRAINT IF EXISTS check_cost_non_negative;");
            migrationBuilder.Sql("ALTER TABLE semantico.migration_execution_histories DROP CONSTRAINT IF EXISTS check_row_counts_non_negative;");

            // ========================================
            // REVERSE POINT 4: Convert JSONB back to TEXT
            // ========================================

            migrationBuilder.Sql(@"
                ALTER TABLE semantico.documentation_agent_runs
                ALTER COLUMN discovered_tables_json TYPE text USING discovered_tables_json::text;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE semantico.documentation_agent_runs
                ALTER COLUMN domain_groups_json TYPE text USING domain_groups_json::text;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE semantico.documentation_agent_runs
                ALTER COLUMN completed_tables_json TYPE text USING completed_tables_json::text;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE semantico.documentation_agent_runs
                ALTER COLUMN failed_tables_json TYPE text USING failed_tables_json::text;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE semantico.documentation_agent_runs
                ALTER COLUMN checkpoint_state_json TYPE text USING checkpoint_state_json::text;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE semantico.ai_prompt_templates
                ALTER COLUMN variable_definitions TYPE text USING variable_definitions::text;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE semantico.ai_conversation_histories
                ALTER COLUMN metadata TYPE text USING metadata::text;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE semantico.data_source_documentations
                ALTER COLUMN metadata TYPE text USING metadata::text;
            ");

            // ========================================
            // REVERSE POINT 6: Drop Indexes
            // ========================================

            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_subscriptions_active_cron;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_qeh_subscription_created;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_anomaly_baselines_sub_time;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_notifications_status_created;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_ai_usage_timestamp_provider;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_recipient_subscription_subscriptions;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_queries_active;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_data_sources_active;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_recipients_active;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_doc_agent_runs_data_source_status;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_doc_agent_runs_discovered_tables_gin;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_doc_agent_runs_domain_groups_gin;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_ai_prompt_templates_variables_gin;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_anomaly_events_unacknowledged;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_query_parameters_query_active;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS semantico.idx_subscription_parameters_subscription_active;");
        }
    }
}
