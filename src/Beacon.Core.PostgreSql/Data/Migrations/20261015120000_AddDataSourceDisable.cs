using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSourceDisable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive only, nothing is backfilled: every existing data source stays enabled (no disabled time).
            migrationBuilder.AddColumn<DateTime>(
                name: "disabled_time",
                schema: "beacon",
                table: "data_sources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "disabled_by_user_id",
                schema: "beacon",
                table: "data_sources",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "disabled_by_user_id",
                schema: "beacon",
                table: "data_sources");

            migrationBuilder.DropColumn(
                name: "disabled_time",
                schema: "beacon",
                table: "data_sources");
        }
    }
}
