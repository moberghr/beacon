using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSchemaRelationship : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SchemaRelationships",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    SourceSchema = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceTable = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceColumn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TargetSchema = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TargetTable = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TargetColumn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Origin = table.Column<int>(type: "int", nullable: false),
                    Cardinality = table.Column<int>(type: "int", nullable: false),
                    ConstraintName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Confidence = table.Column<double>(type: "float", nullable: false),
                    IsVerified = table.Column<bool>(type: "bit", nullable: false),
                    VerifiedByUserId = table.Column<int>(type: "int", nullable: true),
                    VerifiedTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaRelationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaRelationships_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "beacon",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SchemaRelationships_DataSourceId_Origin",
                schema: "beacon",
                table: "SchemaRelationships",
                columns: new[] { "DataSourceId", "Origin" });

            migrationBuilder.CreateIndex(
                name: "IX_SchemaRelationships_DataSourceId_SourceSchema_SourceTable_SourceColumn_TargetSchema_TargetTable_TargetColumn",
                schema: "beacon",
                table: "SchemaRelationships",
                columns: new[] { "DataSourceId", "SourceSchema", "SourceTable", "SourceColumn", "TargetSchema", "TargetTable", "TargetColumn" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SchemaRelationships",
                schema: "beacon");
        }
    }
}
