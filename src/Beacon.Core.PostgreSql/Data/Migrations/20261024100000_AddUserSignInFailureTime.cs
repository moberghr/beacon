using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSignInFailureTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "last_failed_sign_in_time",
                schema: "beacon",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_failed_sign_in_time",
                schema: "beacon",
                table: "users");
        }
    }
}
