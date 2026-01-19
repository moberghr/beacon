using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAllowSampleDataForAi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allow_sample_data_for_ai",
                schema: "semantico",
                table: "data_sources");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allow_sample_data_for_ai",
                schema: "semantico",
                table: "data_sources",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
