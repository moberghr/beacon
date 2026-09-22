using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <summary>
    /// Snapshot-only migration: no DDL, by design.
    ///
    /// PostgreSQL's migrations were scaffolded before the schema became configurable, so every
    /// operation carries a null schema while the runtime model always has one. EF therefore saw
    /// the model and the snapshot disagree and raised PendingModelChangesWarning, which made
    /// UseBeacon()'s Database.Migrate() throw before it could apply anything — even on the
    /// default schema. SQL Server never had this problem; its snapshot already declares
    /// HasDefaultSchema("beacon").
    ///
    /// Scaffolding emitted ~70 RenameTable operations to move every table into "beacon". Those
    /// are wrong here: SchemaAwareMigrationsSqlGenerator already rewrites both the null schema
    /// and the literal "beacon" to the configured schema, so the tables Initial created are
    /// ALREADY in the right place. Executing the renames would emit
    /// ALTER TABLE &lt;configured&gt;.x SET SCHEMA &lt;configured&gt; and fail with
    /// "table is already in schema".
    ///
    /// So Up/Down are intentionally empty. The value of this migration is entirely in its
    /// .Designer.cs and the updated model snapshot, which record the schema and let EF agree
    /// that model == snapshot. Do not "fix" this by regenerating it.
    /// </summary>
    public partial class MoveTablesToConfiguredSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
