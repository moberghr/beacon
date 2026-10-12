using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive, no data rewrite: no existing user must change their password (only passwords an administrator sets
            // from now on are flagged), and existing users keep no stamp until it first changes, so the sessions they hold
            // (which carry none) stay valid until then.
            migrationBuilder.AddColumn<bool>(
                name: "must_change_password",
                schema: "beacon",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "security_stamp",
                schema: "beacon",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "must_change_password",
                schema: "beacon",
                table: "users");

            migrationBuilder.DropColumn(
                name: "security_stamp",
                schema: "beacon",
                table: "users");
        }
    }
}
