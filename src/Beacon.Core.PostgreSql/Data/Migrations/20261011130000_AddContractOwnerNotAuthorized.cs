using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContractOwnerNotAuthorized : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive, no data rewrite: existing contracts have no skipped evaluation recorded.
            migrationBuilder.AddColumn<DateTime>(
                name: "owner_not_authorized_since",
                schema: "beacon",
                table: "data_contracts",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "owner_not_authorized_since",
                schema: "beacon",
                table: "data_contracts");
        }
    }
}
