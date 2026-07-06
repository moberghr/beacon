using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropAiPromptTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiPromptTemplates",
                schema: "beacon");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiPromptTemplates",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    MaxTokens = table.Column<int>(type: "int", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OperationType = table.Column<int>(type: "int", nullable: false),
                    PromptTemplate = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SystemPrompt = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Temperature = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiPromptTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiPromptTemplates_IsActive",
                schema: "beacon",
                table: "AiPromptTemplates",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_AiPromptTemplates_OperationType",
                schema: "beacon",
                table: "AiPromptTemplates",
                column: "OperationType");
        }
    }
}
