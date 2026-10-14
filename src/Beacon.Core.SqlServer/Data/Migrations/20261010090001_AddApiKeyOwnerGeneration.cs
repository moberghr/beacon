using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyOwnerGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApiKeyGeneration",
                schema: "beacon",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OwnerGeneration",
                schema: "beacon",
                table: "ApiKeyCredentials",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerGeneration",
                schema: "beacon",
                table: "ApiKeyCredentials");

            migrationBuilder.DropColumn(
                name: "ApiKeyGeneration",
                schema: "beacon",
                table: "Users");
        }
    }
}
