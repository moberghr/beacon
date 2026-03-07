using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDataContractRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataContractRecipient",
                schema: "semantico",
                columns: table => new
                {
                    DataContractsId = table.Column<int>(type: "int", nullable: false),
                    RecipientsId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataContractRecipient", x => new { x.DataContractsId, x.RecipientsId });
                    table.ForeignKey(
                        name: "FK_DataContractRecipient_DataContracts_DataContractsId",
                        column: x => x.DataContractsId,
                        principalSchema: "semantico",
                        principalTable: "DataContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DataContractRecipient_Recipients_RecipientsId",
                        column: x => x.RecipientsId,
                        principalSchema: "semantico",
                        principalTable: "Recipients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataContractRecipient_RecipientsId",
                schema: "semantico",
                table: "DataContractRecipient",
                column: "RecipientsId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataContractRecipient",
                schema: "semantico");
        }
    }
}
