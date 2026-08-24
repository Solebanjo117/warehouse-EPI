using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WarehouseDbContext))]
[Migration("20260824210000_ExpandWarehouseMapArchitectureStyles")]
public sealed class ExpandWarehouseMapArchitectureStyles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_warehouse_map_architectural_style",
            table: "warehouse_map_architectural_elements");

        migrationBuilder.AddCheckConstraint(
            name: "ck_warehouse_map_architectural_style",
            table: "warehouse_map_architectural_elements",
            sql: "stroke_token IN ('NONE', 'SECONDARY', 'PRIMARY', 'INFO', 'WARNING', 'SUCCESS') AND fill_token IN ('NONE', 'SECONDARY', 'PRIMARY', 'INFO', 'WARNING', 'SUCCESS') AND stroke_width >= 0 AND stroke_width <= 12");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE warehouse_map_architectural_elements
            SET stroke_token = CASE WHEN stroke_token IN ('NONE', 'SECONDARY') THEN stroke_token ELSE 'SECONDARY' END,
                fill_token = CASE WHEN fill_token IN ('NONE', 'SECONDARY') THEN fill_token ELSE 'NONE' END
            WHERE stroke_token NOT IN ('NONE', 'SECONDARY')
               OR fill_token NOT IN ('NONE', 'SECONDARY');
            """);

        migrationBuilder.DropCheckConstraint(
            name: "ck_warehouse_map_architectural_style",
            table: "warehouse_map_architectural_elements");

        migrationBuilder.AddCheckConstraint(
            name: "ck_warehouse_map_architectural_style",
            table: "warehouse_map_architectural_elements",
            sql: "stroke_token IN ('NONE', 'SECONDARY') AND fill_token IN ('NONE', 'SECONDARY') AND stroke_width >= 0 AND stroke_width <= 12");
    }
}
