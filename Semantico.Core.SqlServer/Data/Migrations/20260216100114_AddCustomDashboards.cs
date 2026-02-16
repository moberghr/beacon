using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomDashboards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BodyTemplate",
                schema: "semantico",
                table: "Recipients",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Dashboards",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedByUserName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsShared = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    RefreshIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    LayoutConfiguration = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dashboards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DashboardPermissions",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PermissionLevel = table.Column<int>(type: "int", nullable: false),
                    GrantedByUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    GrantedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardPermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DashboardPermissions_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalSchema: "semantico",
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DashboardWidgets",
                schema: "semantico",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    WidgetType = table.Column<int>(type: "int", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PositionX = table.Column<int>(type: "int", nullable: false),
                    PositionY = table.Column<int>(type: "int", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    RefreshIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardWidgets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DashboardWidgets_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalSchema: "semantico",
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DashboardPermissions_DashboardId",
                schema: "semantico",
                table: "DashboardPermissions",
                column: "DashboardId");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardPermissions_DashboardId_UserId",
                schema: "semantico",
                table: "DashboardPermissions",
                columns: new[] { "DashboardId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DashboardPermissions_UserId",
                schema: "semantico",
                table: "DashboardPermissions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_ArchivedTime",
                schema: "semantico",
                table: "Dashboards",
                column: "ArchivedTime");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_CreatedByUserId",
                schema: "semantico",
                table: "Dashboards",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_IsDefault",
                schema: "semantico",
                table: "Dashboards",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_IsShared",
                schema: "semantico",
                table: "Dashboards",
                column: "IsShared");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardWidgets_DashboardId",
                schema: "semantico",
                table: "DashboardWidgets",
                column: "DashboardId");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardWidgets_DashboardId_SortOrder",
                schema: "semantico",
                table: "DashboardWidgets",
                columns: new[] { "DashboardId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_DashboardWidgets_WidgetType",
                schema: "semantico",
                table: "DashboardWidgets",
                column: "WidgetType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DashboardPermissions",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "DashboardWidgets",
                schema: "semantico");

            migrationBuilder.DropTable(
                name: "Dashboards",
                schema: "semantico");

            migrationBuilder.DropColumn(
                name: "BodyTemplate",
                schema: "semantico",
                table: "Recipients");
        }
    }
}
