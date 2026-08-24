using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WarehouseDbContext))]
[Migration("20260824183000_AddWarehouseMapArchitecture")]
public sealed class AddWarehouseMapArchitecture : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "warehouse_map_layers",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                layout_id = table.Column<short>(type: "smallint", nullable: false),
                code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                sort_order = table.Column<short>(type: "smallint", nullable: false),
                is_locked = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_warehouse_map_layers", x => x.id);
                table.CheckConstraint("ck_warehouse_map_layer_code", "code IN ('STRUCTURE', 'AISLES', 'ZONES', 'TEXT', 'DIMENSIONS', 'OPERATIONS')");
                table.ForeignKey(
                    name: "FK_warehouse_map_layers_warehouse_map_layouts_layout_id",
                    column: x => x.layout_id,
                    principalTable: "warehouse_map_layouts",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "warehouse_map_architectural_elements",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                layout_id = table.Column<short>(type: "smallint", nullable: false),
                layer_id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                geometry_json = table.Column<string>(type: "jsonb", nullable: false),
                stroke_token = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                fill_token = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                stroke_width = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                is_dashed = table.Column<bool>(type: "boolean", nullable: false),
                z_index = table.Column<int>(type: "integer", nullable: false),
                is_locked = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_warehouse_map_architectural_elements", x => x.id);
                table.CheckConstraint("ck_warehouse_map_architectural_kind", "kind IN ('RECTANGLE', 'POLYLINE', 'TEXT')");
                table.CheckConstraint("ck_warehouse_map_architectural_style", "stroke_token IN ('NONE', 'SECONDARY') AND fill_token IN ('NONE', 'SECONDARY') AND stroke_width >= 0 AND stroke_width <= 12");
                table.ForeignKey(
                    name: "FK_warehouse_map_architectural_elements_warehouse_map_layers_layer_id",
                    column: x => x.layer_id,
                    principalTable: "warehouse_map_layers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_warehouse_map_architectural_elements_warehouse_map_layouts_layout_id",
                    column: x => x.layout_id,
                    principalTable: "warehouse_map_layouts",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_warehouse_map_layers_layout_id_code",
            table: "warehouse_map_layers",
            columns: new[] { "layout_id", "code" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_warehouse_map_architectural_elements_layer_id",
            table: "warehouse_map_architectural_elements",
            column: "layer_id");

        migrationBuilder.CreateIndex(
            name: "IX_warehouse_map_architectural_elements_layout_id_layer_id_z_index",
            table: "warehouse_map_architectural_elements",
            columns: new[] { "layout_id", "layer_id", "z_index" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "warehouse_map_architectural_elements");
        migrationBuilder.DropTable(name: "warehouse_map_layers");
    }
}
