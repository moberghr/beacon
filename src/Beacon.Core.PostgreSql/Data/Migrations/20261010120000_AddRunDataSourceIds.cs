using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRunDataSourceIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive: existing runs keep a null value (no data source recorded).
            migrationBuilder.AddColumn<int[]>(
                name: "data_source_ids",
                schema: "beacon",
                table: "query_execution_history",
                type: "integer[]",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "data_source_ids",
                schema: "beacon",
                table: "query_execution_history");
        }
    }
}
