using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbedGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmbedGrants",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PublicId = table.Column<string>(type: "char(32)", unicode: false, fixedLength: true, maxLength: 32, nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    QueryId = table.Column<int>(type: "int", nullable: false),
                    QueryVersionId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AllowedOriginsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FixedParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LockedParameterNamesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SecretHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ExpiresTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastTokenIssuedTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ModifiedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmbedGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmbedGrants_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "beacon",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmbedGrants_Queries_QueryId",
                        column: x => x.QueryId,
                        principalSchema: "beacon",
                        principalTable: "Queries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmbedGrants_QueryVersions_QueryVersionId",
                        column: x => x.QueryVersionId,
                        principalSchema: "beacon",
                        principalTable: "QueryVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmbedGrants_ProjectId_RevokedTime",
                schema: "beacon",
                table: "EmbedGrants",
                columns: new[] { "ProjectId", "RevokedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_EmbedGrants_PublicId",
                schema: "beacon",
                table: "EmbedGrants",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmbedGrants_QueryId",
                schema: "beacon",
                table: "EmbedGrants",
                column: "QueryId");

            migrationBuilder.CreateIndex(
                name: "IX_EmbedGrants_QueryVersionId",
                schema: "beacon",
                table: "EmbedGrants",
                column: "QueryVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_EmbedGrants_SecretHash",
                schema: "beacon",
                table: "EmbedGrants",
                column: "SecretHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmbedGrants",
                schema: "beacon");
        }
    }
}
