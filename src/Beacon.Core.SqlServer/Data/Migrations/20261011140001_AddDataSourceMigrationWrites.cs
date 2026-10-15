using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSourceMigrationWrites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive, no data rewrite: no data source is a migration write target until an Admin marks it.
            migrationBuilder.AddColumn<bool>(
                name: "AllowMigrationWrites",
                schema: "beacon",
                table: "DataSources",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowMigrationWrites",
                schema: "beacon",
                table: "DataSources");
        }
    }
}
