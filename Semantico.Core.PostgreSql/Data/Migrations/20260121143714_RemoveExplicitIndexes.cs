using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveExplicitIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_query_step_change_history_query_step_id",
                schema: "semantico",
                table: "query_step_change_history");

            migrationBuilder.DropIndex(
                name: "ix_documentation_agent_runs_data_source_id",
                schema: "semantico",
                table: "documentation_agent_runs");

            migrationBuilder.DropIndex(
                name: "ix_ai_actors_data_source_id",
                schema: "semantico",
                table: "ai_actors");

            migrationBuilder.DropIndex(
                name: "ix_ai_actor_plans_ai_actor_id",
                schema: "semantico",
                table: "ai_actor_plans");

            migrationBuilder.DropIndex(
                name: "ix_ai_actor_executions_ai_actor_id",
                schema: "semantico",
                table: "ai_actor_executions");

            migrationBuilder.DropIndex(
                name: "ix_ai_actor_conversations_ai_actor_id",
                schema: "semantico",
                table: "ai_actor_conversations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_query_step_change_history_query_step_id",
                schema: "semantico",
                table: "query_step_change_history",
                column: "query_step_id");

            migrationBuilder.CreateIndex(
                name: "ix_documentation_agent_runs_data_source_id",
                schema: "semantico",
                table: "documentation_agent_runs",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actors_data_source_id",
                schema: "semantico",
                table: "ai_actors",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_plans_ai_actor_id",
                schema: "semantico",
                table: "ai_actor_plans",
                column: "ai_actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_executions_ai_actor_id",
                schema: "semantico",
                table: "ai_actor_executions",
                column: "ai_actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_conversations_ai_actor_id",
                schema: "semantico",
                table: "ai_actor_conversations",
                column: "ai_actor_id");
        }
    }
}
