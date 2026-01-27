using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class Cloudwatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ConnectionString",
                schema: "semantico",
                table: "DataSources",
                newName: "EncryptedConnectionData");

            migrationBuilder.AlterColumn<int>(
                name: "DatabaseEngineType",
                schema: "semantico",
                table: "DataSources",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<int>(
                name: "DataSourceType",
                schema: "semantico",
                table: "DataSources",
                type: "int",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DataSourceType",
                schema: "semantico",
                table: "DataSources");

            migrationBuilder.RenameColumn(
                name: "EncryptedConnectionData",
                schema: "semantico",
                table: "DataSources",
                newName: "ConnectionString");

            migrationBuilder.AlterColumn<int>(
                name: "DatabaseEngineType",
                schema: "semantico",
                table: "DataSources",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
