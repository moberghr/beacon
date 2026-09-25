using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHostManagedDataSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HostManagedKey",
                schema: "beacon",
                table: "DataSources",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostModelHash",
                schema: "beacon",
                table: "DataSources",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DataSources_HostManagedKey",
                schema: "beacon",
                table: "DataSources",
                column: "HostManagedKey",
                unique: true,
                filter: "[HostManagedKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DataSources_HostManagedKey",
                schema: "beacon",
                table: "DataSources");

            migrationBuilder.DropColumn(
                name: "HostManagedKey",
                schema: "beacon",
                table: "DataSources");

            migrationBuilder.DropColumn(
                name: "HostModelHash",
                schema: "beacon",
                table: "DataSources");
        }
    }
}
