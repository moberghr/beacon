using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdentityProvider",
                schema: "semantico",
                table: "Users",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_IdentityProvider_ExternalId",
                schema: "semantico",
                table: "Users",
                columns: new[] { "IdentityProvider", "ExternalId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_IdentityProvider_ExternalId",
                schema: "semantico",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IdentityProvider",
                schema: "semantico",
                table: "Users");
        }
    }
}
