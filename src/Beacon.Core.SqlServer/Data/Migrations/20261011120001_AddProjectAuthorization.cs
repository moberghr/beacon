using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive, no data rewrite. Existing subscriptions keep no owner and existing versions no recorded sources.
            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                schema: "beacon",
                table: "Subscriptions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DataSourceIds",
                schema: "beacon",
                table: "QueryVersions",
                type: "nvarchar(max)",
                nullable: true);

            // Existing queries keep no owner, existing links no linking user, and existing change history no recorded
            // data source.
            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                schema: "beacon",
                table: "Queries",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LinkedByUserId",
                schema: "beacon",
                table: "ProjectDataSources",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DataSourceId",
                schema: "beacon",
                table: "QueryStepChangeHistory",
                type: "int",
                nullable: true);

            // Existing projects stay open.
            migrationBuilder.AddColumn<bool>(
                name: "RequiresMembership",
                schema: "beacon",
                table: "Projects",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Existing data sources stay reachable while no project links them (true). The model has no store default:
            // every data source created from now on sets its own value, false.
            migrationBuilder.AddColumn<bool>(
                name: "OpenWhenUnlinked",
                schema: "beacon",
                table: "DataSources",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "ProjectMembers",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    AddedByUserId = table.Column<int>(type: "int", nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_members", x => x.Id);
                    table.ForeignKey(
                        name: "fk_project_members_projects_project_id",
                        column: x => x.ProjectId,
                        principalSchema: "beacon",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_project_members_users_user_id",
                        column: x => x.UserId,
                        principalSchema: "beacon",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_owner_user_id",
                schema: "beacon",
                table: "Subscriptions",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "ix_project_members_user_id",
                schema: "beacon",
                table: "ProjectMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "ux_project_members_project_id_user_id",
                schema: "beacon",
                table: "ProjectMembers",
                columns: new[] { "ProjectId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectMembers",
                schema: "beacon");

            migrationBuilder.DropIndex(
                name: "ix_subscriptions_owner_user_id",
                schema: "beacon",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                schema: "beacon",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "DataSourceIds",
                schema: "beacon",
                table: "QueryVersions");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                schema: "beacon",
                table: "Queries");

            migrationBuilder.DropColumn(
                name: "LinkedByUserId",
                schema: "beacon",
                table: "ProjectDataSources");

            migrationBuilder.DropColumn(
                name: "DataSourceId",
                schema: "beacon",
                table: "QueryStepChangeHistory");

            migrationBuilder.DropColumn(
                name: "RequiresMembership",
                schema: "beacon",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "OpenWhenUnlinked",
                schema: "beacon",
                table: "DataSources");
        }
    }
}
