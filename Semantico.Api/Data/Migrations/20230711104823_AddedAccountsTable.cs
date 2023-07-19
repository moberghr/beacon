using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddedAccountsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    username = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                });

            migrationBuilder.InsertData(
                schema: "semantico",
                table: "accounts",
                columns: new[] { "id", "created_time", "username", "value" },
                values: new object[] { 1, new DateTime(2023, 7, 11, 10, 48, 23, 809, DateTimeKind.Utc).AddTicks(5305), "moberg", "AQAAAAIAAYagAAAAECWEQ1jq8CPkruy8QrQy4eQqwKjFAQ2tt8wW/tH7zCype5L2asjL4W9+uBdvLMvPNQ==" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accounts",
                schema: "semantico");
        }
    }
}
