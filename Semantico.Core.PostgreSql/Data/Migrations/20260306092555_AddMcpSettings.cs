using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mcp_settings",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ask_system_prompt = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    global_instruction = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    list_data_sources_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    query_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    get_documentation_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ask_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    max_row_limit = table.Column<int>(type: "integer", nullable: false),
                    enforce_read_only = table.Column<bool>(type: "boolean", nullable: false),
                    enable_pii_detection = table.Column<bool>(type: "boolean", nullable: false),
                    custom_pii_patterns = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_settings", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_settings",
                schema: "semantico");
        }
    }
}
