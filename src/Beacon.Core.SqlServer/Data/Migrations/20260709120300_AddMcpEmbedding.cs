using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpEmbedding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpEmbeddings",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    OwnerType = table.Column<int>(type: "int", nullable: false),
                    OwnerId = table.Column<int>(type: "int", nullable: false),
                    EmbeddingBytes = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    EmbeddingVersion = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpEmbeddings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpEmbeddings_DataSourceId",
                schema: "beacon",
                table: "McpEmbeddings",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_McpEmbeddings_DataSourceId_OwnerType_OwnerId",
                schema: "beacon",
                table: "McpEmbeddings",
                columns: new[] { "DataSourceId", "OwnerType", "OwnerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpEmbeddings",
                schema: "beacon");
        }
    }
}
