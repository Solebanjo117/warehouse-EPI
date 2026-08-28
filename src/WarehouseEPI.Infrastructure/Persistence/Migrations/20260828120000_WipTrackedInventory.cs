using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WarehouseDbContext))]
[Migration("20260828120000_WipTrackedInventory")]
public sealed class WipTrackedInventory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_inventory_movements_operational_shape",
            table: "inventory_movements");
        migrationBuilder.DropCheckConstraint(
            name: "ck_inventory_movements_purpose",
            table: "inventory_movements");

        migrationBuilder.AddCheckConstraint(
            name: "ck_inventory_movements_purpose",
            table: "inventory_movements",
            sql: "purpose IN ('STANDARD', 'GENERAL_EXIT', 'PRODUCTION_ISSUE', 'WIP_WAREHOUSE_RETURN', 'WIP_CONSUMPTION', 'WIP_SUPPLIER_RETURN', 'CYCLE_COUNT_ADJUSTMENT')");
        migrationBuilder.AddCheckConstraint(
            name: "ck_inventory_movements_operational_shape",
            table: "inventory_movements",
            sql: "(purpose = 'PRODUCTION_ISSUE' AND type IN ('ENTRY', 'EXIT', 'TRANSFER') AND operational_area_id IS NOT NULL) OR " +
                 "(purpose = 'GENERAL_EXIT' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR " +
                 "(purpose = 'WIP_WAREHOUSE_RETURN' AND ((type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (type = 'TRANSFER' AND operational_area_id IS NOT NULL))) OR " +
                 "(purpose = 'WIP_CONSUMPTION' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL) OR " +
                 "(purpose = 'WIP_SUPPLIER_RETURN' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL AND NULLIF(BTRIM(reference), '') IS NOT NULL) OR " +
                 "(purpose = 'STANDARD' AND operational_area_id IS NULL) OR " +
                 "(purpose = 'CYCLE_COUNT_ADJUSTMENT' AND type = 'ADJUSTMENT' AND operational_area_id IS NULL)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_inventory_movements_operational_shape",
            table: "inventory_movements");
        migrationBuilder.DropCheckConstraint(
            name: "ck_inventory_movements_purpose",
            table: "inventory_movements");

        migrationBuilder.AddCheckConstraint(
            name: "ck_inventory_movements_purpose",
            table: "inventory_movements",
            sql: "purpose IN ('STANDARD', 'GENERAL_EXIT', 'PRODUCTION_ISSUE', 'WIP_WAREHOUSE_RETURN', 'CYCLE_COUNT_ADJUSTMENT')");
        migrationBuilder.AddCheckConstraint(
            name: "ck_inventory_movements_operational_shape",
            table: "inventory_movements",
            sql: "(purpose = 'PRODUCTION_ISSUE' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL) OR " +
                 "(purpose = 'GENERAL_EXIT' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR " +
                 "(purpose = 'WIP_WAREHOUSE_RETURN' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR " +
                 "(purpose = 'STANDARD' AND operational_area_id IS NULL) OR " +
                 "(purpose = 'CYCLE_COUNT_ADJUSTMENT' AND type = 'ADJUSTMENT' AND operational_area_id IS NULL)");
    }
}
