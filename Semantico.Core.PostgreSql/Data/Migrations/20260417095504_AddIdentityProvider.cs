using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "identity_provider",
                schema: "semantico",
                table: "users",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_identity_provider_external_id",
                schema: "semantico",
                table: "users",
                columns: new[] { "identity_provider", "external_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_identity_provider_external_id",
                schema: "semantico",
                table: "users");

            migrationBuilder.DropColumn(
                name: "identity_provider",
                schema: "semantico",
                table: "users");
        }
    }
}
