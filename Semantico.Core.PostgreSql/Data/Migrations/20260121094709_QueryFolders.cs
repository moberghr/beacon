using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class QueryFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "folder_id",
                schema: "semantico",
                table: "queries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "query_folders",
                schema: "semantico",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    parent_folder_id = table.Column<int>(type: "integer", nullable: true),
                    path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_query_folders", x => x.id);
                    table.ForeignKey(
                        name: "fk_query_folders_query_folders_parent_folder_id",
                        column: x => x.parent_folder_id,
                        principalSchema: "semantico",
                        principalTable: "query_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_queries_folder_id",
                schema: "semantico",
                table: "queries",
                column: "folder_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_folders_archived_time",
                schema: "semantico",
                table: "query_folders",
                column: "archived_time");

            migrationBuilder.CreateIndex(
                name: "ix_query_folders_parent_folder_id",
                schema: "semantico",
                table: "query_folders",
                column: "parent_folder_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_folders_parent_folder_id_name",
                schema: "semantico",
                table: "query_folders",
                columns: new[] { "parent_folder_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_query_folders_parent_folder_id_sort_order",
                schema: "semantico",
                table: "query_folders",
                columns: new[] { "parent_folder_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_query_folders_path",
                schema: "semantico",
                table: "query_folders",
                column: "path");

            migrationBuilder.AddForeignKey(
                name: "fk_queries_query_folders_folder_id",
                schema: "semantico",
                table: "queries",
                column: "folder_id",
                principalSchema: "semantico",
                principalTable: "query_folders",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_queries_query_folders_folder_id",
                schema: "semantico",
                table: "queries");

            migrationBuilder.DropTable(
                name: "query_folders",
                schema: "semantico");

            migrationBuilder.DropIndex(
                name: "ix_queries_folder_id",
                schema: "semantico",
                table: "queries");

            migrationBuilder.DropColumn(
                name: "folder_id",
                schema: "semantico",
                table: "queries");
        }
    }
}
