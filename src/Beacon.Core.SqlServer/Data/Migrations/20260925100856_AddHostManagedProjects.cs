using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHostManagedProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HostManagedKey",
                schema: "beacon",
                table: "Projects",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_HostManagedKey",
                schema: "beacon",
                table: "Projects",
                column: "HostManagedKey",
                unique: true,
                filter: "[HostManagedKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Projects_HostManagedKey",
                schema: "beacon",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "HostManagedKey",
                schema: "beacon",
                table: "Projects");
        }
    }
}
