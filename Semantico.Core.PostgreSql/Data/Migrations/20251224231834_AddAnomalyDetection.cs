using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnomalyDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "anomaly_baselines",
                schema: "semantico",
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
                    table.ForeignKey(
                        name: "fk_anomaly_baselines_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "semantico",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "anomaly_configs",
                schema: "semantico",
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
                    table.ForeignKey(
                        name: "fk_anomaly_configs_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "semantico",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "anomaly_events",
                schema: "semantico",
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
                    acknowledged_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_anomaly_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_anomaly_events_notifications_notification_id",
                        column: x => x.notification_id,
                        principalSchema: "semantico",
                        principalTable: "notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_anomaly_events_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "semantico",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_baselines_execution_time",
                schema: "semantico",
                table: "anomaly_baselines",
                column: "execution_time");

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_baselines_subscription_id_execution_time",
                schema: "semantico",
                table: "anomaly_baselines",
                columns: new[] { "subscription_id", "execution_time" });

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_configs_enabled",
                schema: "semantico",
                table: "anomaly_configs",
                column: "enabled");

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_configs_subscription_id",
                schema: "semantico",
                table: "anomaly_configs",
                column: "subscription_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_events_acknowledged_detected_time",
                schema: "semantico",
                table: "anomaly_events",
                columns: new[] { "acknowledged", "detected_time" });

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_events_detected_time",
                schema: "semantico",
                table: "anomaly_events",
                column: "detected_time");

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_events_notification_id",
                schema: "semantico",
                table: "anomaly_events",
                column: "notification_id");

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_events_subscription_id_detected_time",
                schema: "semantico",
                table: "anomaly_events",
                columns: new[] { "subscription_id", "detected_time" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "anomaly_baselines",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "anomaly_configs",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "anomaly_events",
                schema: "semantico");
        }
    }
}
