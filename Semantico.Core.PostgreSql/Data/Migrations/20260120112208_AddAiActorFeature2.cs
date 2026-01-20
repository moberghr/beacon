using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiActorFeature2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_locked",
                schema: "semantico",
                table: "queries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "locked_at",
                schema: "semantico",
                table: "queries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "locked_by_user_id",
                schema: "semantico",
                table: "queries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "requires_approval",
                schema: "semantico",
                table: "ai_actors",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ai_actor_plan_id",
                schema: "semantico",
                table: "ai_actor_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "detailed_analysis",
                schema: "semantico",
                table: "ai_actor_executions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "findings_json",
                schema: "semantico",
                table: "ai_actor_executions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_actor_plans",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "ai_actor_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_ai_actor_plans_ai_actors_ai_actor_id",
                        column: x => x.ai_actor_id,
                        principalSchema: "semantico",
                        principalTable: "ai_actors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "query_step_change_history",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "ai_actor_executions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_query_step_change_history_ai_actor_plans_ai_actor_plan_id",
                        column: x => x.ai_actor_plan_id,
                        principalSchema: "semantico",
                        principalTable: "ai_actor_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_query_step_change_history_ai_actors_ai_actor_id",
                        column: x => x.ai_actor_id,
                        principalSchema: "semantico",
                        principalTable: "ai_actors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_query_step_change_history_query_steps_query_step_id",
                        column: x => x.query_step_id,
                        principalSchema: "semantico",
                        principalTable: "query_steps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_queries_is_locked",
                schema: "semantico",
                table: "queries",
                column: "is_locked");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_executions_ai_actor_plan_id",
                schema: "semantico",
                table: "ai_actor_executions",
                column: "ai_actor_plan_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_plans_ai_actor_id",
                schema: "semantico",
                table: "ai_actor_plans",
                column: "ai_actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_plans_ai_actor_id_proposed_at",
                schema: "semantico",
                table: "ai_actor_plans",
                columns: new[] { "ai_actor_id", "proposed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_plans_ai_actor_id_status",
                schema: "semantico",
                table: "ai_actor_plans",
                columns: new[] { "ai_actor_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_plans_parent_plan_id",
                schema: "semantico",
                table: "ai_actor_plans",
                column: "parent_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_plans_proposed_at",
                schema: "semantico",
                table: "ai_actor_plans",
                column: "proposed_at");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_plans_status",
                schema: "semantico",
                table: "ai_actor_plans",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_query_step_change_history_ai_actor_execution_id",
                schema: "semantico",
                table: "query_step_change_history",
                column: "ai_actor_execution_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_step_change_history_ai_actor_id",
                schema: "semantico",
                table: "query_step_change_history",
                column: "ai_actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_step_change_history_ai_actor_plan_id",
                schema: "semantico",
                table: "query_step_change_history",
                column: "ai_actor_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_step_change_history_change_source",
                schema: "semantico",
                table: "query_step_change_history",
                column: "change_source");

            migrationBuilder.CreateIndex(
                name: "ix_query_step_change_history_changed_at",
                schema: "semantico",
                table: "query_step_change_history",
                column: "changed_at");

            migrationBuilder.CreateIndex(
                name: "ix_query_step_change_history_query_step_id",
                schema: "semantico",
                table: "query_step_change_history",
                column: "query_step_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_step_change_history_query_step_id_changed_at",
                schema: "semantico",
                table: "query_step_change_history",
                columns: new[] { "query_step_id", "changed_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_ai_actor_executions_ai_actor_plans_ai_actor_plan_id",
                schema: "semantico",
                table: "ai_actor_executions",
                column: "ai_actor_plan_id",
                principalSchema: "semantico",
                principalTable: "ai_actor_plans",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_ai_actor_executions_ai_actor_plans_ai_actor_plan_id",
                schema: "semantico",
                table: "ai_actor_executions");

            migrationBuilder.DropTable(
                name: "query_step_change_history",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "ai_actor_plans",
                schema: "semantico");

            migrationBuilder.DropIndex(
                name: "ix_queries_is_locked",
                schema: "semantico",
                table: "queries");

            migrationBuilder.DropIndex(
                name: "ix_ai_actor_executions_ai_actor_plan_id",
                schema: "semantico",
                table: "ai_actor_executions");

            migrationBuilder.DropColumn(
                name: "is_locked",
                schema: "semantico",
                table: "queries");

            migrationBuilder.DropColumn(
                name: "locked_at",
                schema: "semantico",
                table: "queries");

            migrationBuilder.DropColumn(
                name: "locked_by_user_id",
                schema: "semantico",
                table: "queries");

            migrationBuilder.DropColumn(
                name: "requires_approval",
                schema: "semantico",
                table: "ai_actors");

            migrationBuilder.DropColumn(
                name: "ai_actor_plan_id",
                schema: "semantico",
                table: "ai_actor_executions");

            migrationBuilder.DropColumn(
                name: "detailed_analysis",
                schema: "semantico",
                table: "ai_actor_executions");

            migrationBuilder.DropColumn(
                name: "findings_json",
                schema: "semantico",
                table: "ai_actor_executions");
        }
    }
}
