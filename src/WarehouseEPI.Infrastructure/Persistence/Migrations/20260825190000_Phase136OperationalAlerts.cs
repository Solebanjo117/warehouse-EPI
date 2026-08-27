using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WarehouseDbContext))]
[Migration("20260825190000_Phase136OperationalAlerts")]
public sealed class Phase136OperationalAlerts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "wip_reminder_days",
            table: "business_settings",
            type: "integer",
            nullable: false,
            defaultValue: 7);

        migrationBuilder.AddCheckConstraint(
            name: "ck_business_settings_wip_reminder_days",
            table: "business_settings",
            sql: "wip_reminder_days BETWEEN 1 AND 365");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_business_settings_wip_reminder_days",
            table: "business_settings");
        migrationBuilder.DropColumn(name: "wip_reminder_days", table: "business_settings");
    }
}
