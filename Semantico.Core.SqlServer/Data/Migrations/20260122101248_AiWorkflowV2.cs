using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiWorkflowV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QueryStepChangeHistory_QueryStepId",
                schema: "semantico",
                table: "QueryStepChangeHistory");

            migrationBuilder.DropIndex(
                name: "IX_DocumentationAgentRuns_DataSourceId",
                schema: "semantico",
                table: "DocumentationAgentRuns");

            migrationBuilder.DropIndex(
                name: "IX_AiActors_DataSourceId",
                schema: "semantico",
                table: "AiActors");

            migrationBuilder.DropIndex(
                name: "IX_AiActorPlans_AiActorId",
                schema: "semantico",
                table: "AiActorPlans");

            migrationBuilder.DropIndex(
                name: "IX_AiActorExecutions_AiActorId",
                schema: "semantico",
                table: "AiActorExecutions");

            migrationBuilder.DropIndex(
                name: "IX_AiActorConversations_AiActorId",
                schema: "semantico",
                table: "AiActorConversations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_QueryStepChangeHistory_QueryStepId",
                schema: "semantico",
                table: "QueryStepChangeHistory",
                column: "QueryStepId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationAgentRuns_DataSourceId",
                schema: "semantico",
                table: "DocumentationAgentRuns",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_AiActors_DataSourceId",
                schema: "semantico",
                table: "AiActors",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorPlans_AiActorId",
                schema: "semantico",
                table: "AiActorPlans",
                column: "AiActorId");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorExecutions_AiActorId",
                schema: "semantico",
                table: "AiActorExecutions",
                column: "AiActorId");

            migrationBuilder.CreateIndex(
                name: "IX_AiActorConversations_AiActorId",
                schema: "semantico",
                table: "AiActorConversations",
                column: "AiActorId");
        }
    }
}
