using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropAiPromptTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_prompt_templates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_prompt_templates",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    max_tokens = table.Column<int>(type: "integer", nullable: false),
                    modified_by = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    operation_type = table.Column<int>(type: "integer", nullable: false),
                    prompt_template = table.Column<string>(type: "text", nullable: false),
                    system_prompt = table.Column<string>(type: "text", nullable: true),
                    temperature = table.Column<decimal>(type: "numeric", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_prompt_templates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_prompt_templates_is_active",
                table: "ai_prompt_templates",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_ai_prompt_templates_operation_type",
                table: "ai_prompt_templates",
                column: "operation_type");
        }
    }
}
