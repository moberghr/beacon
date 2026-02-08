using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_setting_history",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    setting_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    old_value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    new_value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    changed_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_setting_history", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "app_settings",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    is_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_app_setting_history_changed_at",
                schema: "semantico",
                table: "app_setting_history",
                column: "changed_at");

            migrationBuilder.CreateIndex(
                name: "ix_app_setting_history_setting_key",
                schema: "semantico",
                table: "app_setting_history",
                column: "setting_key");

            migrationBuilder.CreateIndex(
                name: "ix_app_settings_category",
                schema: "semantico",
                table: "app_settings",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_app_settings_key",
                schema: "semantico",
                table: "app_settings",
                column: "key",
                unique: true);

            // Seed default settings
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff+00");
            migrationBuilder.Sql($@"
                INSERT INTO semantico.app_settings (key, value, category, is_sensitive, created_time) VALUES
                -- General
                ('General.BaseUrl', NULL, 'General', false, '{now}'),
                -- LLM
                ('LLM.Provider', NULL, 'LLM', false, '{now}'),
                ('LLM.ApiKey', NULL, 'LLM', true, '{now}'),
                ('LLM.Endpoint', NULL, 'LLM', true, '{now}'),
                ('LLM.Region', NULL, 'LLM', false, '{now}'),
                ('LLM.SessionToken', NULL, 'LLM', true, '{now}'),
                ('LLM.Model', NULL, 'LLM', false, '{now}'),
                ('LLM.FastModel', NULL, 'LLM', false, '{now}'),
                ('LLM.MaxConcurrentRequests', '50', 'LLM', false, '{now}'),
                ('LLM.TokensPerMinute', '80000', 'LLM', false, '{now}'),
                ('LLM.RequestsPerMinute', '1000', 'LLM', false, '{now}'),
                ('LLM.MonthlyBudget', '100.00', 'LLM', false, '{now}');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_setting_history",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "app_settings",
                schema: "semantico");
        }
    }
}
