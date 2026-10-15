using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSourceMigrationWrites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive, no data rewrite: no data source is a migration write target until an Admin marks it.
            migrationBuilder.AddColumn<bool>(
                name: "allow_migration_writes",
                schema: "beacon",
                table: "data_sources",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allow_migration_writes",
                schema: "beacon",
                table: "data_sources");
        }
    }
}
