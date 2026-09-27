using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHostManagedDataSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "host_managed_key",
                schema: "beacon",
                table: "data_sources",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "host_model_hash",
                schema: "beacon",
                table: "data_sources",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_data_sources_host_managed_key",
                schema: "beacon",
                table: "data_sources",
                column: "host_managed_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_data_sources_host_managed_key",
                schema: "beacon",
                table: "data_sources");

            migrationBuilder.DropColumn(
                name: "host_managed_key",
                schema: "beacon",
                table: "data_sources");

            migrationBuilder.DropColumn(
                name: "host_model_hash",
                schema: "beacon",
                table: "data_sources");
        }
    }
}
