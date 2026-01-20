using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiActorFeature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ai_actor_id",
                schema: "semantico",
                table: "subscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ai_actor_id",
                schema: "semantico",
                table: "queries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_actors",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_actor_executions",
                schema: "semantico",
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
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_actor_executions", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_actor_executions_ai_actors_ai_actor_id",
                        column: x => x.ai_actor_id,
                        principalSchema: "semantico",
                        principalTable: "ai_actors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ai_actor_executions_subscriptions_triggering_subscription_id",
                        column: x => x.triggering_subscription_id,
                        principalSchema: "semantico",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ai_actor_conversations",
                schema: "semantico",
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
                        name: "fk_ai_actor_conversations_ai_actor_executions_ai_actor_executi",
                        column: x => x.ai_actor_execution_id,
                        principalSchema: "semantico",
                        principalTable: "ai_actor_executions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_ai_actor_conversations_ai_actors_ai_actor_id",
                        column: x => x.ai_actor_id,
                        principalSchema: "semantico",
                        principalTable: "ai_actors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_ai_actor_id",
                schema: "semantico",
                table: "subscriptions",
                column: "ai_actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_queries_ai_actor_id",
                schema: "semantico",
                table: "queries",
                column: "ai_actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_conversations_ai_actor_execution_id",
                schema: "semantico",
                table: "ai_actor_conversations",
                column: "ai_actor_execution_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_conversations_ai_actor_id",
                schema: "semantico",
                table: "ai_actor_conversations",
                column: "ai_actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_conversations_ai_actor_id_turn_number",
                schema: "semantico",
                table: "ai_actor_conversations",
                columns: new[] { "ai_actor_id", "turn_number" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_conversations_timestamp",
                schema: "semantico",
                table: "ai_actor_conversations",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_conversations_turn_number",
                schema: "semantico",
                table: "ai_actor_conversations",
                column: "turn_number");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_executions_ai_actor_id",
                schema: "semantico",
                table: "ai_actor_executions",
                column: "ai_actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_executions_ai_actor_id_started_at",
                schema: "semantico",
                table: "ai_actor_executions",
                columns: new[] { "ai_actor_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_executions_phase",
                schema: "semantico",
                table: "ai_actor_executions",
                column: "phase");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_executions_started_at",
                schema: "semantico",
                table: "ai_actor_executions",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actor_executions_triggering_subscription_id",
                schema: "semantico",
                table: "ai_actor_executions",
                column: "triggering_subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actors_archived_time",
                schema: "semantico",
                table: "ai_actors",
                column: "archived_time");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actors_data_source_id",
                schema: "semantico",
                table: "ai_actors",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actors_data_source_id_status",
                schema: "semantico",
                table: "ai_actors",
                columns: new[] { "data_source_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_actors_status",
                schema: "semantico",
                table: "ai_actors",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_ai_actors_status_archived_time",
                schema: "semantico",
                table: "ai_actors",
                columns: new[] { "status", "archived_time" });

            migrationBuilder.AddForeignKey(
                name: "fk_queries_ai_actors_ai_actor_id",
                schema: "semantico",
                table: "queries",
                column: "ai_actor_id",
                principalSchema: "semantico",
                principalTable: "ai_actors",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_subscriptions_ai_actors_ai_actor_id",
                schema: "semantico",
                table: "subscriptions",
                column: "ai_actor_id",
                principalSchema: "semantico",
                principalTable: "ai_actors",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_queries_ai_actors_ai_actor_id",
                schema: "semantico",
                table: "queries");

            migrationBuilder.DropForeignKey(
                name: "fk_subscriptions_ai_actors_ai_actor_id",
                schema: "semantico",
                table: "subscriptions");

            migrationBuilder.DropTable(
                name: "ai_actor_conversations",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "ai_actor_executions",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "ai_actors",
                schema: "semantico");

            migrationBuilder.DropIndex(
                name: "ix_subscriptions_ai_actor_id",
                schema: "semantico",
                table: "subscriptions");

            migrationBuilder.DropIndex(
                name: "ix_queries_ai_actor_id",
                schema: "semantico",
                table: "queries");

            migrationBuilder.DropColumn(
                name: "ai_actor_id",
                schema: "semantico",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "ai_actor_id",
                schema: "semantico",
                table: "queries");
        }
    }
}
