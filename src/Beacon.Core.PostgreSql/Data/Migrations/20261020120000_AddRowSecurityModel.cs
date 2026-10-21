using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRowSecurityModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "row_security_version",
                schema: "beacon",
                table: "data_sources",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "attribute_definitions",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    value_type = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    modified_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    modified_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attribute_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "table_classifications",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    schema_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    table_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    @class = table.Column<int>(name: "class", type: "integer", nullable: true),
                    requested_class = table.Column<int>(type: "integer", nullable: true),
                    created_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    modified_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    modified_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_table_classifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_table_classifications_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalSchema: "beacon",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_attribute_values",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    attribute_definition_id = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_attribute_values", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_attribute_values_attribute_definitions_attribute_defin",
                        column: x => x.attribute_definition_id,
                        principalSchema: "beacon",
                        principalTable: "attribute_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_attribute_values_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "beacon",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "row_policies",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    schema_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    table_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    active_version_id = table.Column<int>(type: "integer", nullable: true),
                    created_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    modified_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    modified_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_row_policies", x => x.id);
                    table.ForeignKey(
                        name: "fk_row_policies_data_sources_data_source_id",
                        column: x => x.data_source_id,
                        principalSchema: "beacon",
                        principalTable: "data_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "row_policy_versions",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    row_policy_id = table.Column<int>(type: "integer", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    definition_json = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_row_policy_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_row_policy_versions_row_policies_row_policy_id",
                        column: x => x.row_policy_id,
                        principalSchema: "beacon",
                        principalTable: "row_policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attribute_definitions_key",
                schema: "beacon",
                table: "attribute_definitions",
                column: "key",
                unique: true,
                filter: "archived_time IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_row_policies_active_version_id",
                schema: "beacon",
                table: "row_policies",
                column: "active_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_row_policies_data_source_id_schema_name_table_name",
                schema: "beacon",
                table: "row_policies",
                columns: new[] { "data_source_id", "schema_name", "table_name" });

            migrationBuilder.CreateIndex(
                name: "ix_row_policy_versions_row_policy_id_status",
                schema: "beacon",
                table: "row_policy_versions",
                columns: new[] { "row_policy_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_row_policy_versions_row_policy_id_version_number",
                schema: "beacon",
                table: "row_policy_versions",
                columns: new[] { "row_policy_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_table_classifications_data_source_id_schema_name_table_name",
                schema: "beacon",
                table: "table_classifications",
                columns: new[] { "data_source_id", "schema_name", "table_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_attribute_values_attribute_definition_id",
                schema: "beacon",
                table: "user_attribute_values",
                column: "attribute_definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_attribute_values_user_id_attribute_definition_id_value",
                schema: "beacon",
                table: "user_attribute_values",
                columns: new[] { "user_id", "attribute_definition_id", "value" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_row_policies_row_policy_versions_active_version_id",
                schema: "beacon",
                table: "row_policies",
                column: "active_version_id",
                principalSchema: "beacon",
                principalTable: "row_policy_versions",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_row_policies_row_policy_versions_active_version_id",
                schema: "beacon",
                table: "row_policies");

            migrationBuilder.DropTable(
                name: "table_classifications",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "user_attribute_values",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "attribute_definitions",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "row_policy_versions",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "row_policies",
                schema: "beacon");

            migrationBuilder.DropColumn(
                name: "row_security_version",
                schema: "beacon",
                table: "data_sources");
        }
    }
}
