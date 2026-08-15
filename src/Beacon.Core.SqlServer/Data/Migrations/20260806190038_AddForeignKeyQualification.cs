using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddForeignKeyQualification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ForeignKeyConstraintName",
                schema: "beacon",
                table: "ColumnMetadata",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ForeignKeySchema",
                schema: "beacon",
                table: "ColumnMetadata",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForeignKeyConstraintName",
                schema: "beacon",
                table: "ColumnMetadata");

            migrationBuilder.DropColumn(
                name: "ForeignKeySchema",
                schema: "beacon",
                table: "ColumnMetadata");
        }
    }
}
