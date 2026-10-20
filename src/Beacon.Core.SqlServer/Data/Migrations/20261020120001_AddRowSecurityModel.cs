using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRowSecurityModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RowSecurityVersion",
                schema: "beacon",
                table: "DataSources",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "AttributeDefinitions",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ValueType = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ModifiedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttributeDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TableClassifications",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    SchemaName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Class = table.Column<int>(type: "int", nullable: true),
                    RequestedClass = table.Column<int>(type: "int", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ModifiedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TableClassifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TableClassifications_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "beacon",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserAttributeValues",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    AttributeDefinitionId = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAttributeValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserAttributeValues_AttributeDefinitions_AttributeDefinitionId",
                        column: x => x.AttributeDefinitionId,
                        principalSchema: "beacon",
                        principalTable: "AttributeDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserAttributeValues_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "beacon",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RowPolicies",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    SchemaName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ActiveVersionId = table.Column<int>(type: "int", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ModifiedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RowPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RowPolicies_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "beacon",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RowPolicyVersions",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RowPolicyId = table.Column<int>(type: "int", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RowPolicyVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RowPolicyVersions_RowPolicies_RowPolicyId",
                        column: x => x.RowPolicyId,
                        principalSchema: "beacon",
                        principalTable: "RowPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttributeDefinitions_Key",
                schema: "beacon",
                table: "AttributeDefinitions",
                column: "Key",
                unique: true,
                filter: "[ArchivedTime] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RowPolicies_ActiveVersionId",
                schema: "beacon",
                table: "RowPolicies",
                column: "ActiveVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_RowPolicies_DataSourceId_SchemaName_TableName",
                schema: "beacon",
                table: "RowPolicies",
                columns: new[] { "DataSourceId", "SchemaName", "TableName" });

            migrationBuilder.CreateIndex(
                name: "IX_RowPolicyVersions_RowPolicyId_Status",
                schema: "beacon",
                table: "RowPolicyVersions",
                columns: new[] { "RowPolicyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RowPolicyVersions_RowPolicyId_VersionNumber",
                schema: "beacon",
                table: "RowPolicyVersions",
                columns: new[] { "RowPolicyId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TableClassifications_DataSourceId_SchemaName_TableName",
                schema: "beacon",
                table: "TableClassifications",
                columns: new[] { "DataSourceId", "SchemaName", "TableName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserAttributeValues_AttributeDefinitionId",
                schema: "beacon",
                table: "UserAttributeValues",
                column: "AttributeDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_UserAttributeValues_UserId_AttributeDefinitionId_Value",
                schema: "beacon",
                table: "UserAttributeValues",
                columns: new[] { "UserId", "AttributeDefinitionId", "Value" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_RowPolicies_RowPolicyVersions_ActiveVersionId",
                schema: "beacon",
                table: "RowPolicies",
                column: "ActiveVersionId",
                principalSchema: "beacon",
                principalTable: "RowPolicyVersions",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RowPolicies_RowPolicyVersions_ActiveVersionId",
                schema: "beacon",
                table: "RowPolicies");

            migrationBuilder.DropTable(
                name: "TableClassifications",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "UserAttributeValues",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "AttributeDefinitions",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "RowPolicyVersions",
                schema: "beacon");

            migrationBuilder.DropTable(
                name: "RowPolicies",
                schema: "beacon");

            migrationBuilder.DropColumn(
                name: "RowSecurityVersion",
                schema: "beacon",
                table: "DataSources");
        }
    }
}
