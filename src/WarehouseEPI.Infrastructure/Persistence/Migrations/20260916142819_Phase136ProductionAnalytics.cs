using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase136ProductionAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "inactivity_alert_hours",
                table: "production_stages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "rework_alert_hours",
                table: "production_stages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_production_stages_alert_hours",
                table: "production_stages",
                sql: "(inactivity_alert_hours IS NULL OR inactivity_alert_hours > 0) AND (rework_alert_hours IS NULL OR rework_alert_hours > 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_production_stages_alert_hours",
                table: "production_stages");

            migrationBuilder.DropColumn(
                name: "inactivity_alert_hours",
                table: "production_stages");

            migrationBuilder.DropColumn(
                name: "rework_alert_hours",
                table: "production_stages");
        }
    }
}
