using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHostManagedProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "host_managed_key",
                schema: "beacon",
                table: "projects",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_projects_host_managed_key",
                schema: "beacon",
                table: "projects",
                column: "host_managed_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_projects_host_managed_key",
                schema: "beacon",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "host_managed_key",
                schema: "beacon",
                table: "projects");
        }
    }
}
