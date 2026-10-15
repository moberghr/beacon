using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContractOwnerNotAuthorized : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive, no data rewrite: existing contracts have no skipped evaluation recorded.
            migrationBuilder.AddColumn<DateTime>(
                name: "OwnerNotAuthorizedSince",
                schema: "beacon",
                table: "DataContracts",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerNotAuthorizedSince",
                schema: "beacon",
                table: "DataContracts");
        }
    }
}
