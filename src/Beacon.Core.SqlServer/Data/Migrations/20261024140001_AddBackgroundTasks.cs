using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBackgroundTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackgroundTasks",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    SubjectId = table.Column<int>(type: "int", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    JobId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    StartedTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    WarningCount = table.Column<int>(type: "int", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundTasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundTasks_Kind_SubjectId",
                schema: "beacon",
                table: "BackgroundTasks",
                columns: new[] { "Kind", "SubjectId" },
                unique: true,
                filter: "[State] IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackgroundTasks",
                schema: "beacon");
        }
    }
}
