using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRunDataSourceIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive: existing runs keep a null value (no data source recorded).
            migrationBuilder.AddColumn<string>(
                name: "DataSourceIds",
                schema: "beacon",
                table: "QueryExecutionHistory",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DataSourceIds",
                schema: "beacon",
                table: "QueryExecutionHistory");
        }
    }
}
