using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSourceDisable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive only, nothing is backfilled: every existing data source stays enabled (no disabled time).
            migrationBuilder.AddColumn<DateTime>(
                name: "DisabledTime",
                schema: "beacon",
                table: "DataSources",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DisabledByUserId",
                schema: "beacon",
                table: "DataSources",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisabledByUserId",
                schema: "beacon",
                table: "DataSources");

            migrationBuilder.DropColumn(
                name: "DisabledTime",
                schema: "beacon",
                table: "DataSources");
        }
    }
}
