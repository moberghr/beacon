using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnableCrossProjectQueryChains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FIRST: Create the new QueryStep and QueryStepParameter tables

            migrationBuilder.CreateTable(
                name: "query_steps",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    query_id = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: false),
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
                        name: "fk_query_steps_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "semantico",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_query_steps_queries_query_id",
                        column: x => x.query_id,
                        principalSchema: "semantico",
                        principalTable: "queries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "query_step_parameters",
                schema: "semantico",
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
                        principalSchema: "semantico",
                        principalTable: "query_steps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_query_step_parameters_query_step_id",
                schema: "semantico",
                table: "query_step_parameters",
                column: "query_step_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_steps_project_id",
                schema: "semantico",
                table: "query_steps",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_steps_query_id",
                schema: "semantico",
                table: "query_steps",
                column: "query_id");

            // SECOND: Migrate existing query data to query steps
            migrationBuilder.Sql(@"
                INSERT INTO semantico.query_steps (query_id, project_id, step_order, sql_value, name, description, created_time)
                SELECT 
                    id as query_id,
                    project_id,
                    1 as step_order,
                    sql_value,
                    'Step 1' as name,
                    'Migrated from legacy query' as description,
                    created_time
                FROM semantico.queries
                WHERE archived_time IS NULL
                  AND project_id IS NOT NULL
                  AND sql_value IS NOT NULL;
            ");

            // THIRD: Migrate existing query parameters to query step parameters
            migrationBuilder.Sql(@"
                INSERT INTO semantico.query_step_parameters (query_step_id, name, type, description, placeholder, created_time)
                SELECT 
                    qs.id as query_step_id,
                    qp.name,
                    qp.type,
                    qp.description,
                    qp.placeholder,
                    qp.created_time
                FROM semantico.query_parameters qp
                INNER JOIN semantico.query_steps qs ON qs.query_id = qp.query_id AND qs.step_order = 1
                WHERE qp.archived_time IS NULL;
            ");

            // FOURTH: Now we can safely drop the old columns and constraints
            migrationBuilder.DropForeignKey(
                name: "fk_queries_projects_project_id",
                schema: "semantico",
                table: "queries");

            migrationBuilder.DropIndex(
                name: "ix_queries_project_id",
                schema: "semantico",
                table: "queries");

            migrationBuilder.DropColumn(
                name: "project_id",
                schema: "semantico",
                table: "queries");

            migrationBuilder.DropColumn(
                name: "sql_value",
                schema: "semantico",
                table: "queries");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "query_step_parameters",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "query_steps",
                schema: "semantico");

            migrationBuilder.AddColumn<int>(
                name: "project_id",
                schema: "semantico",
                table: "queries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "sql_value",
                schema: "semantico",
                table: "queries",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_queries_project_id",
                schema: "semantico",
                table: "queries",
                column: "project_id");

            migrationBuilder.AddForeignKey(
                name: "fk_queries_projects_project_id",
                schema: "semantico",
                table: "queries",
                column: "project_id",
                principalSchema: "semantico",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
