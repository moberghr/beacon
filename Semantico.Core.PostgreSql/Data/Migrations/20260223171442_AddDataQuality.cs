using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDataQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_contracts",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "data_quality_scores",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "data_contract_rules",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "data_contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "data_quality_evaluations",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "data_contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "data_quality_rule_results",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "data_contract_rules",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_data_quality_rule_results_data_quality_evaluations_data_qua",
                        column: x => x.data_quality_evaluation_id,
                        principalSchema: "semantico",
                        principalTable: "data_quality_evaluations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_data_contract_rules_data_contract_id",
                schema: "semantico",
                table: "data_contract_rules",
                column: "data_contract_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_contracts_archived_time",
                schema: "semantico",
                table: "data_contracts",
                column: "archived_time");

            migrationBuilder.CreateIndex(
                name: "ix_data_contracts_data_source_id",
                schema: "semantico",
                table: "data_contracts",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_contracts_data_source_id_schema_name_table_name",
                schema: "semantico",
                table: "data_contracts",
                columns: new[] { "data_source_id", "schema_name", "table_name" });

            migrationBuilder.CreateIndex(
                name: "ix_data_contracts_is_enabled",
                schema: "semantico",
                table: "data_contracts",
                column: "is_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_data_quality_evaluations_created_time",
                schema: "semantico",
                table: "data_quality_evaluations",
                column: "created_time");

            migrationBuilder.CreateIndex(
                name: "ix_data_quality_evaluations_data_contract_id",
                schema: "semantico",
                table: "data_quality_evaluations",
                column: "data_contract_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_quality_rule_results_data_contract_rule_id",
                schema: "semantico",
                table: "data_quality_rule_results",
                column: "data_contract_rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_quality_rule_results_data_quality_evaluation_id",
                schema: "semantico",
                table: "data_quality_rule_results",
                column: "data_quality_evaluation_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_quality_scores_data_source_id_schema_name_table_name",
                schema: "semantico",
                table: "data_quality_scores",
                columns: new[] { "data_source_id", "schema_name", "table_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_data_quality_scores_evaluated_at",
                schema: "semantico",
                table: "data_quality_scores",
                column: "evaluated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_quality_rule_results",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "data_quality_scores",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "data_contract_rules",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "data_quality_evaluations",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "data_contracts",
                schema: "semantico");
        }
    }
}
