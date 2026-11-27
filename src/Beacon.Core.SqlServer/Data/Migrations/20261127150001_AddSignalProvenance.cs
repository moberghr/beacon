using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSignalProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FeedbackReason",
                schema: "beacon",
                table: "McpQuerySignals",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsEditedRerun",
                schema: "beacon",
                table: "McpQuerySignals",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ParentSignalId",
                schema: "beacon",
                table: "McpQuerySignals",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "McpSignalContextItems",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SignalId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpSignalContextItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpSignalContextItems_McpQuerySignals_SignalId",
                        column: x => x.SignalId,
                        principalSchema: "beacon",
                        principalTable: "McpQuerySignals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpQuerySignals_ParentSignalId",
                schema: "beacon",
                table: "McpQuerySignals",
                column: "ParentSignalId");

            migrationBuilder.CreateIndex(
                name: "IX_McpSignalContextItems_Kind_ItemId",
                schema: "beacon",
                table: "McpSignalContextItems",
                columns: new[] { "Kind", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_McpSignalContextItems_SignalId",
                schema: "beacon",
                table: "McpSignalContextItems",
                column: "SignalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpSignalContextItems",
                schema: "beacon");

            migrationBuilder.DropIndex(
                name: "IX_McpQuerySignals_ParentSignalId",
                schema: "beacon",
                table: "McpQuerySignals");

            migrationBuilder.DropColumn(
                name: "FeedbackReason",
                schema: "beacon",
                table: "McpQuerySignals");

            migrationBuilder.DropColumn(
                name: "IsEditedRerun",
                schema: "beacon",
                table: "McpQuerySignals");

            migrationBuilder.DropColumn(
                name: "ParentSignalId",
                schema: "beacon",
                table: "McpQuerySignals");
        }
    }
}
