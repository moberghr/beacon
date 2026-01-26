using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class RefactorAi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_ai_actor_conversations_ai_actor_executions_ai_actor_executi",
                schema: "semantico",
                table: "ai_actor_conversations");

            migrationBuilder.DropForeignKey(
                name: "fk_ai_actor_executions_ai_actor_plans_ai_actor_plan_id",
                schema: "semantico",
                table: "ai_actor_executions");

            migrationBuilder.DropForeignKey(
                name: "fk_ai_actor_plans_ai_actor_plans_parent_plan_id",
                schema: "semantico",
                table: "ai_actor_plans");

            migrationBuilder.DropForeignKey(
                name: "fk_anomaly_events_subscriptions_subscription_id",
                schema: "semantico",
                table: "anomaly_events");

            migrationBuilder.DropForeignKey(
                name: "fk_documentation_agent_runs_data_sources_data_source_id",
                schema: "semantico",
                table: "documentation_agent_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_query_step_change_history_ai_actor_executions_ai_actor_exec",
                schema: "semantico",
                table: "query_step_change_history");

            migrationBuilder.DropForeignKey(
                name: "fk_query_step_change_history_ai_actor_plans_ai_actor_plan_id",
                schema: "semantico",
                table: "query_step_change_history");

            migrationBuilder.DropForeignKey(
                name: "fk_query_step_change_history_ai_actors_ai_actor_id",
                schema: "semantico",
                table: "query_step_change_history");

            migrationBuilder.AddForeignKey(
                name: "fk_ai_actor_conversations_ai_actor_executions_ai_actor_executi",
                schema: "semantico",
                table: "ai_actor_conversations",
                column: "ai_actor_execution_id",
                principalSchema: "semantico",
                principalTable: "ai_actor_executions",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_ai_actor_executions_ai_actor_plans_ai_actor_plan_id",
                schema: "semantico",
                table: "ai_actor_executions",
                column: "ai_actor_plan_id",
                principalSchema: "semantico",
                principalTable: "ai_actor_plans",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_ai_actor_plans_ai_actor_plans_parent_plan_id",
                schema: "semantico",
                table: "ai_actor_plans",
                column: "parent_plan_id",
                principalSchema: "semantico",
                principalTable: "ai_actor_plans",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_anomaly_events_subscriptions_subscription_id",
                schema: "semantico",
                table: "anomaly_events",
                column: "subscription_id",
                principalSchema: "semantico",
                principalTable: "subscriptions",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_documentation_agent_runs_data_sources_data_source_id",
                schema: "semantico",
                table: "documentation_agent_runs",
                column: "data_source_id",
                principalSchema: "semantico",
                principalTable: "data_sources",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_query_step_change_history_ai_actor_executions_ai_actor_exec",
                schema: "semantico",
                table: "query_step_change_history",
                column: "ai_actor_execution_id",
                principalSchema: "semantico",
                principalTable: "ai_actor_executions",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_query_step_change_history_ai_actor_plans_ai_actor_plan_id",
                schema: "semantico",
                table: "query_step_change_history",
                column: "ai_actor_plan_id",
                principalSchema: "semantico",
                principalTable: "ai_actor_plans",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_query_step_change_history_ai_actors_ai_actor_id",
                schema: "semantico",
                table: "query_step_change_history",
                column: "ai_actor_id",
                principalSchema: "semantico",
                principalTable: "ai_actors",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_ai_actor_conversations_ai_actor_executions_ai_actor_executi",
                schema: "semantico",
                table: "ai_actor_conversations");

            migrationBuilder.DropForeignKey(
                name: "fk_ai_actor_executions_ai_actor_plans_ai_actor_plan_id",
                schema: "semantico",
                table: "ai_actor_executions");

            migrationBuilder.DropForeignKey(
                name: "fk_ai_actor_plans_ai_actor_plans_parent_plan_id",
                schema: "semantico",
                table: "ai_actor_plans");

            migrationBuilder.DropForeignKey(
                name: "fk_anomaly_events_subscriptions_subscription_id",
                schema: "semantico",
                table: "anomaly_events");

            migrationBuilder.DropForeignKey(
                name: "fk_documentation_agent_runs_data_sources_data_source_id",
                schema: "semantico",
                table: "documentation_agent_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_query_step_change_history_ai_actor_executions_ai_actor_exec",
                schema: "semantico",
                table: "query_step_change_history");

            migrationBuilder.DropForeignKey(
                name: "fk_query_step_change_history_ai_actor_plans_ai_actor_plan_id",
                schema: "semantico",
                table: "query_step_change_history");

            migrationBuilder.DropForeignKey(
                name: "fk_query_step_change_history_ai_actors_ai_actor_id",
                schema: "semantico",
                table: "query_step_change_history");

            migrationBuilder.AddForeignKey(
                name: "fk_ai_actor_conversations_ai_actor_executions_ai_actor_executi",
                schema: "semantico",
                table: "ai_actor_conversations",
                column: "ai_actor_execution_id",
                principalSchema: "semantico",
                principalTable: "ai_actor_executions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_ai_actor_executions_ai_actor_plans_ai_actor_plan_id",
                schema: "semantico",
                table: "ai_actor_executions",
                column: "ai_actor_plan_id",
                principalSchema: "semantico",
                principalTable: "ai_actor_plans",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_ai_actor_plans_ai_actor_plans_parent_plan_id",
                schema: "semantico",
                table: "ai_actor_plans",
                column: "parent_plan_id",
                principalSchema: "semantico",
                principalTable: "ai_actor_plans",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_anomaly_events_subscriptions_subscription_id",
                schema: "semantico",
                table: "anomaly_events",
                column: "subscription_id",
                principalSchema: "semantico",
                principalTable: "subscriptions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_documentation_agent_runs_data_sources_data_source_id",
                schema: "semantico",
                table: "documentation_agent_runs",
                column: "data_source_id",
                principalSchema: "semantico",
                principalTable: "data_sources",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_query_step_change_history_ai_actor_executions_ai_actor_exec",
                schema: "semantico",
                table: "query_step_change_history",
                column: "ai_actor_execution_id",
                principalSchema: "semantico",
                principalTable: "ai_actor_executions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_query_step_change_history_ai_actor_plans_ai_actor_plan_id",
                schema: "semantico",
                table: "query_step_change_history",
                column: "ai_actor_plan_id",
                principalSchema: "semantico",
                principalTable: "ai_actor_plans",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_query_step_change_history_ai_actors_ai_actor_id",
                schema: "semantico",
                table: "query_step_change_history",
                column: "ai_actor_id",
                principalSchema: "semantico",
                principalTable: "ai_actors",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
