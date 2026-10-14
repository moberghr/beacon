using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <summary>
    /// Data only, no schema change. Custom SQL rules saved before they were restricted to Admins and checked by the
    /// read-only gate are switched off, so none runs again until an Admin re-enables it by saving the contract, which
    /// re-validates its SQL. The contract itself stays enabled. Rule type 8 is <c>DataContractRuleType.CustomSql</c>.
    /// </summary>
    public partial class DisableCustomSqlRulesForReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE beacon.data_contract_rules SET is_enabled = false WHERE rule_type = 8;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: which rules were enabled before Up() ran is not recorded, so it cannot be restored.
        }
    }
}
