using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.Migrations
{
    /// <inheritdoc />
    public partial class AccountChangeToApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "semantico",
                table: "accounts",
                keyColumn: "id",
                keyValue: 1,
                column: "value",
                value: "00000000-0000-0000-0000-000000000000");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "semantico",
                table: "accounts",
                keyColumn: "id",
                keyValue: 1,
                column: "value",
                value: "AQAAAAIAAYagAAAAECWEQ1jq8CPkruy8QrQy4eQqwKjFAQ2tt8wW/tH7zCype5L2asjL4W9+uBdvLMvPNQ==");
        }
    }
}
