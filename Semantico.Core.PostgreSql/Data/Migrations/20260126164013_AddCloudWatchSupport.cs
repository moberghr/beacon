using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudWatchSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "connection_string",
                schema: "semantico",
                table: "data_sources",
                newName: "encrypted_connection_data");

            migrationBuilder.AlterColumn<int>(
                name: "database_engine_type",
                schema: "semantico",
                table: "data_sources",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "data_source_type",
                schema: "semantico",
                table: "data_sources",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "data_source_type",
                schema: "semantico",
                table: "data_sources");

            migrationBuilder.RenameColumn(
                name: "encrypted_connection_data",
                schema: "semantico",
                table: "data_sources",
                newName: "connection_string");

            migrationBuilder.AlterColumn<int>(
                name: "database_engine_type",
                schema: "semantico",
                table: "data_sources",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
