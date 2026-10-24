using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbedGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "embed_grants",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<string>(type: "character(32)", unicode: false, fixedLength: true, maxLength: 32, nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    query_id = table.Column<int>(type: "integer", nullable: false),
                    query_version_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    allowed_origins_json = table.Column<string>(type: "text", nullable: false),
                    fixed_parameters_json = table.Column<string>(type: "text", nullable: false),
                    locked_parameter_names_json = table.Column<string>(type: "text", nullable: false),
                    secret_hash = table.Column<string>(type: "character(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    expires_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    last_token_issued_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    modified_by_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    modified_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embed_grants", x => x.id);
                    table.ForeignKey(
                        name: "fk_embed_grants_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "beacon",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_embed_grants_queries_query_id",
                        column: x => x.query_id,
                        principalSchema: "beacon",
                        principalTable: "queries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_embed_grants_query_versions_query_version_id",
                        column: x => x.query_version_id,
                        principalSchema: "beacon",
                        principalTable: "query_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_embed_grants_project_id_revoked_time",
                schema: "beacon",
                table: "embed_grants",
                columns: new[] { "project_id", "revoked_time" });

            migrationBuilder.CreateIndex(
                name: "ix_embed_grants_public_id",
                schema: "beacon",
                table: "embed_grants",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_embed_grants_query_id",
                schema: "beacon",
                table: "embed_grants",
                column: "query_id");

            migrationBuilder.CreateIndex(
                name: "ix_embed_grants_query_version_id",
                schema: "beacon",
                table: "embed_grants",
                column: "query_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_embed_grants_secret_hash",
                schema: "beacon",
                table: "embed_grants",
                column: "secret_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "embed_grants",
                schema: "beacon");
        }
    }
}
