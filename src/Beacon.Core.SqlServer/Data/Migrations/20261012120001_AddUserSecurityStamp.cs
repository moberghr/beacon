using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
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
                name: "MustChangePassword",
                schema: "beacon",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                schema: "beacon",
                table: "Users",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                schema: "beacon",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                schema: "beacon",
                table: "Users");
        }
    }
}
