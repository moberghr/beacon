using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKbTier3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProjectId",
                schema: "beacon",
                table: "McpEmbeddings",
                type: "int",
                nullable: true);

            // Project-scoped doc-chunk / glossary embeddings leave DataSourceId null (symmetric with the
            // nullable ProjectId) instead of the old magic-sentinel 0.
            migrationBuilder.AlterColumn<int>(
                name: "DataSourceId",
                schema: "beacon",
                table: "McpEmbeddings",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnableContextualRetrieval",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DocChunkWindowSentences",
                schema: "beacon",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "DocChunkOverlapSentences",
                schema: "beacon",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "GlossaryTopK",
                schema: "beacon",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "DocChunkTopK",
                schema: "beacon",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.CreateTable(
                name: "McpDocChunks",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    SourceSectionId = table.Column<int>(type: "int", nullable: false),
                    ChunkText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContextualBlurb = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpDocChunks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpGlossaryTerms",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    DataSourceId = table.Column<int>(type: "int", nullable: true),
                    Term = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Synonyms = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Definition = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    TargetSchema = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TargetTable = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TargetColumn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MetricExpression = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpGlossaryTerms", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpEmbeddings_ProjectId_OwnerType",
                schema: "beacon",
                table: "McpEmbeddings",
                columns: new[] { "ProjectId", "OwnerType" });

            migrationBuilder.CreateIndex(
                name: "IX_McpDocChunks_ProjectId",
                schema: "beacon",
                table: "McpDocChunks",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_McpDocChunks_ProjectId_SourceSectionId",
                schema: "beacon",
                table: "McpDocChunks",
                columns: new[] { "ProjectId", "SourceSectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_McpGlossaryTerms_ProjectId",
                schema: "beacon",
                table: "McpGlossaryTerms",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_McpGlossaryTerms_ProjectId_IsActive",
                schema: "beacon",
                table: "McpGlossaryTerms",
                columns: new[] { "ProjectId", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpDocChunks",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "McpGlossaryTerms",
                schema: "beacon");

            migrationBuilder.DropIndex(
                name: "IX_McpEmbeddings_ProjectId_OwnerType",
                schema: "beacon",
                table: "McpEmbeddings");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                schema: "beacon",
                table: "McpEmbeddings");

            migrationBuilder.AlterColumn<int>(
                name: "DataSourceId",
                schema: "beacon",
                table: "McpEmbeddings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "EnableContextualRetrieval",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "DocChunkWindowSentences",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "DocChunkOverlapSentences",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "GlossaryTopK",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "DocChunkTopK",
                schema: "beacon",
                table: "McpSettings");
        }
    }
}
