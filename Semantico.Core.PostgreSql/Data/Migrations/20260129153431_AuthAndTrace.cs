using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuthAndTrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "manual_query_execution_logs",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    query_text = table.Column<string>(type: "text", nullable: false),
                    result_count = table.Column<int>(type: "integer", nullable: false),
                    execution_time_ms = table.Column<double>(type: "double precision", nullable: false),
                    data_source_id = table.Column<int>(type: "integer", nullable: true),
                    execution_context = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manual_query_execution_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_manual_query_execution_logs_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalSchema: "semantico",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_manual_query_execution_logs_created_time",
                schema: "semantico",
                table: "manual_query_execution_logs",
                column: "created_time");

            migrationBuilder.CreateIndex(
                name: "ix_manual_query_execution_logs_data_source_id",
                schema: "semantico",
                table: "manual_query_execution_logs",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_manual_query_execution_logs_data_source_id_created_time",
                schema: "semantico",
                table: "manual_query_execution_logs",
                columns: new[] { "data_source_id", "created_time" });

            migrationBuilder.CreateIndex(
                name: "ix_manual_query_execution_logs_execution_context",
                schema: "semantico",
                table: "manual_query_execution_logs",
                column: "execution_context");

            migrationBuilder.CreateIndex(
                name: "ix_manual_query_execution_logs_user_id",
                schema: "semantico",
                table: "manual_query_execution_logs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_manual_query_execution_logs_user_id_created_time",
                schema: "semantico",
                table: "manual_query_execution_logs",
                columns: new[] { "user_id", "created_time" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "manual_query_execution_logs",
                schema: "semantico");
        }
    }
}
