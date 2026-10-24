using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBackgroundTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "background_tasks",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    subject_id = table.Column<int>(type: "integer", nullable: false),
                    requested_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    state = table.Column<int>(type: "integer", nullable: false),
                    job_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    started_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finished_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    warning_count = table.Column<int>(type: "integer", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_background_tasks", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_background_tasks_kind_subject_id",
                schema: "beacon",
                table: "background_tasks",
                columns: new[] { "kind", "subject_id" },
                unique: true,
                filter: "state IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "background_tasks",
                schema: "beacon");
        }
    }
}
