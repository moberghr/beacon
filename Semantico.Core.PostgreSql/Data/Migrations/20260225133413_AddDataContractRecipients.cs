using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDataContractRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_contract_recipient",
                schema: "semantico",
                columns: table => new
                {
                    data_contracts_id = table.Column<int>(type: "integer", nullable: false),
                    recipients_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_contract_recipient", x => new { x.data_contracts_id, x.recipients_id });
                    table.ForeignKey(
                        name: "fk_data_contract_recipient_data_contracts_data_contracts_id",
                        column: x => x.data_contracts_id,
                        principalSchema: "semantico",
                        principalTable: "data_contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_data_contract_recipient_recipients_recipients_id",
                        column: x => x.recipients_id,
                        principalSchema: "semantico",
                        principalTable: "recipients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_data_contract_recipient_recipients_id",
                schema: "semantico",
                table: "data_contract_recipient",
                column: "recipients_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_contract_recipient",
                schema: "semantico");
        }
    }
}
