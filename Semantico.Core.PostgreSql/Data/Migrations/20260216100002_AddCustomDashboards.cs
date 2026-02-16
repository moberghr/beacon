using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomDashboards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dashboards",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_by_user_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_shared = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    refresh_interval_seconds = table.Column<int>(type: "integer", nullable: true),
                    layout_configuration = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dashboards", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dashboard_permissions",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    dashboard_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    permission_level = table.Column<int>(type: "integer", nullable: false),
                    granted_by_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    granted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dashboard_permissions", x => x.id);
                    table.ForeignKey(
                        name: "fk_dashboard_permissions_dashboards_dashboard_id",
                        column: x => x.dashboard_id,
                        principalSchema: "semantico",
                        principalTable: "dashboards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dashboard_widgets",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    dashboard_id = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    widget_type = table.Column<int>(type: "integer", nullable: false),
                    configuration_json = table.Column<string>(type: "text", nullable: false),
                    position_x = table.Column<int>(type: "integer", nullable: false),
                    position_y = table.Column<int>(type: "integer", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    refresh_interval_seconds = table.Column<int>(type: "integer", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dashboard_widgets", x => x.id);
                    table.ForeignKey(
                        name: "fk_dashboard_widgets_dashboards_dashboard_id",
                        column: x => x.dashboard_id,
                        principalSchema: "semantico",
                        principalTable: "dashboards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_permissions_dashboard_id",
                schema: "semantico",
                table: "dashboard_permissions",
                column: "dashboard_id");

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_permissions_dashboard_id_user_id",
                schema: "semantico",
                table: "dashboard_permissions",
                columns: new[] { "dashboard_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_permissions_user_id",
                schema: "semantico",
                table: "dashboard_permissions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_widgets_dashboard_id",
                schema: "semantico",
                table: "dashboard_widgets",
                column: "dashboard_id");

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_widgets_dashboard_id_sort_order",
                schema: "semantico",
                table: "dashboard_widgets",
                columns: new[] { "dashboard_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_widgets_widget_type",
                schema: "semantico",
                table: "dashboard_widgets",
                column: "widget_type");

            migrationBuilder.CreateIndex(
                name: "ix_dashboards_archived_time",
                schema: "semantico",
                table: "dashboards",
                column: "archived_time");

            migrationBuilder.CreateIndex(
                name: "ix_dashboards_created_by_user_id",
                schema: "semantico",
                table: "dashboards",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_dashboards_is_default",
                schema: "semantico",
                table: "dashboards",
                column: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_dashboards_is_shared",
                schema: "semantico",
                table: "dashboards",
                column: "is_shared");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dashboard_permissions",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "dashboard_widgets",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "dashboards",
                schema: "semantico");
        }
    }
}
