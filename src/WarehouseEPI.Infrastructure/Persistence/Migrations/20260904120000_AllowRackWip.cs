using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WarehouseDbContext))]
[Migration("20260904120000_AllowRackWip")]
public sealed class AllowRackWip : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_locations_wip_area",
            table: "locations");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE locations
            SET operational_role = 'STORAGE', updated_at = CURRENT_TIMESTAMP
            WHERE kind = 'RACK' AND operational_role = 'WIP';
            """);

        migrationBuilder.AddCheckConstraint(
            name: "ck_locations_wip_area",
            table: "locations",
            sql: "operational_role <> 'WIP' OR kind = 'AREA'");
    }
}
