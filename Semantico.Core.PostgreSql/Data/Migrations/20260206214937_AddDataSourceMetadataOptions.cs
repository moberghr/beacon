using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSourceMetadataOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "metadata_exclude_schemas",
                schema: "semantico",
                table: "data_sources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "metadata_include_schemas",
                schema: "semantico",
                table: "data_sources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "metadata_load_table_names_only",
                schema: "semantico",
                table: "data_sources",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "metadata_loading_enabled",
                schema: "semantico",
                table: "data_sources",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "metadata_max_columns_per_table",
                schema: "semantico",
                table: "data_sources",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "metadata_max_tables",
                schema: "semantico",
                table: "data_sources",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "metadata_exclude_schemas",
                schema: "semantico",
                table: "data_sources");

            migrationBuilder.DropColumn(
                name: "metadata_include_schemas",
                schema: "semantico",
                table: "data_sources");

            migrationBuilder.DropColumn(
                name: "metadata_load_table_names_only",
                schema: "semantico",
                table: "data_sources");

            migrationBuilder.DropColumn(
                name: "metadata_loading_enabled",
                schema: "semantico",
                table: "data_sources");

            migrationBuilder.DropColumn(
                name: "metadata_max_columns_per_table",
                schema: "semantico",
                table: "data_sources");

            migrationBuilder.DropColumn(
                name: "metadata_max_tables",
                schema: "semantico",
                table: "data_sources");
        }
    }
}
