using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WarehouseDbContext))]
[Migration("20260824233000_AddWarehouseMapProductivityScale")]
public sealed class AddWarehouseMapProductivityScale : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "measurement_system",
            table: "warehouse_map_layouts",
            type: "character varying(10)",
            maxLength: 10,
            nullable: false,
            defaultValue: "IMPERIAL");

        migrationBuilder.AddColumn<decimal>(
            name: "scale_units_per_inch",
            table: "warehouse_map_layouts",
            type: "numeric(12,6)",
            precision: 12,
            scale: 6,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "group_id",
            table: "warehouse_map_architectural_elements",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "is_archived",
            table: "warehouse_map_architectural_elements",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddCheckConstraint(
            name: "ck_warehouse_map_layout_measurement",
            table: "warehouse_map_layouts",
            sql: "measurement_system IN ('IMPERIAL', 'METRIC')");

        migrationBuilder.AddCheckConstraint(
            name: "ck_warehouse_map_layout_scale",
            table: "warehouse_map_layouts",
            sql: "scale_units_per_inch IS NULL OR scale_units_per_inch > 0");

        migrationBuilder.CreateIndex(
            name: "ix_warehouse_map_architectural_elements_layout_id_group_id",
            table: "warehouse_map_architectural_elements",
            columns: new[] { "layout_id", "group_id" },
            filter: "group_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_warehouse_map_architectural_elements_layout_id_is_archived",
            table: "warehouse_map_architectural_elements",
            columns: new[] { "layout_id", "is_archived" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_warehouse_map_architectural_elements_layout_id_group_id",
            table: "warehouse_map_architectural_elements");

        migrationBuilder.DropIndex(
            name: "ix_warehouse_map_architectural_elements_layout_id_is_archived",
            table: "warehouse_map_architectural_elements");

        migrationBuilder.DropCheckConstraint(
            name: "ck_warehouse_map_layout_measurement",
            table: "warehouse_map_layouts");

        migrationBuilder.DropCheckConstraint(
            name: "ck_warehouse_map_layout_scale",
            table: "warehouse_map_layouts");

        migrationBuilder.DropColumn(name: "measurement_system", table: "warehouse_map_layouts");
        migrationBuilder.DropColumn(name: "scale_units_per_inch", table: "warehouse_map_layouts");
        migrationBuilder.DropColumn(name: "group_id", table: "warehouse_map_architectural_elements");
        migrationBuilder.DropColumn(name: "is_archived", table: "warehouse_map_architectural_elements");
    }
}
