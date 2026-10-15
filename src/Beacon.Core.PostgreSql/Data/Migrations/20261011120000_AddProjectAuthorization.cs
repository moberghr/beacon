using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive, no data rewrite. Existing subscriptions keep no owner and existing versions no recorded sources.
            migrationBuilder.AddColumn<string>(
                name: "owner_user_id",
                schema: "beacon",
                table: "subscriptions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int[]>(
                name: "data_source_ids",
                schema: "beacon",
                table: "query_versions",
                type: "integer[]",
                nullable: true);

            // Existing queries keep no owner, existing links no linking user, and existing change history no recorded
            // data source.
            migrationBuilder.AddColumn<string>(
                name: "owner_user_id",
                schema: "beacon",
                table: "queries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "linked_by_user_id",
                schema: "beacon",
                table: "project_data_sources",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "data_source_id",
                schema: "beacon",
                table: "query_step_change_history",
                type: "integer",
                nullable: true);

            // Existing projects stay open.
            migrationBuilder.AddColumn<bool>(
                name: "requires_membership",
                schema: "beacon",
                table: "projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Existing data sources stay reachable while no project links them (true). The model has no store default:
            // every data source created from now on sets its own value, false.
            migrationBuilder.AddColumn<bool>(
                name: "open_when_unlinked",
                schema: "beacon",
                table: "data_sources",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "project_members",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    added_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    modified_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_members", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_members_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "beacon",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_project_members_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "beacon",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_owner_user_id",
                schema: "beacon",
                table: "subscriptions",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_members_user_id",
                schema: "beacon",
                table: "project_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_project_members_project_id_user_id",
                schema: "beacon",
                table: "project_members",
                columns: new[] { "project_id", "user_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_members",
                schema: "beacon");

            migrationBuilder.DropIndex(
                name: "ix_subscriptions_owner_user_id",
                schema: "beacon",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "owner_user_id",
                schema: "beacon",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "data_source_ids",
                schema: "beacon",
                table: "query_versions");

            migrationBuilder.DropColumn(
                name: "owner_user_id",
                schema: "beacon",
                table: "queries");

            migrationBuilder.DropColumn(
                name: "linked_by_user_id",
                schema: "beacon",
                table: "project_data_sources");

            migrationBuilder.DropColumn(
                name: "data_source_id",
                schema: "beacon",
                table: "query_step_change_history");

            migrationBuilder.DropColumn(
                name: "requires_membership",
                schema: "beacon",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "open_when_unlinked",
                schema: "beacon",
                table: "data_sources");
        }
    }
}
