using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWarehouseMapCanvasDimensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "canvas_height",
                table: "warehouse_map_layouts",
                type: "numeric(8,2)",
                precision: 8,
                scale: 2,
                nullable: false,
                defaultValue: 900m);

            migrationBuilder.AddColumn<decimal>(
                name: "canvas_width",
                table: "warehouse_map_layouts",
                type: "numeric(8,2)",
                precision: 8,
                scale: 2,
                nullable: false,
                defaultValue: 1600m);

            migrationBuilder.AddCheckConstraint(
                name: "ck_warehouse_map_layout_canvas",
                table: "warehouse_map_layouts",
                sql: "canvas_width BETWEEN 1600 AND 6400 AND canvas_height BETWEEN 900 AND 3600 AND canvas_width % 25 = 0 AND canvas_height % 25 = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_warehouse_map_layout_canvas",
                table: "warehouse_map_layouts");

            migrationBuilder.DropColumn(
                name: "canvas_height",
                table: "warehouse_map_layouts");

            migrationBuilder.DropColumn(
                name: "canvas_width",
                table: "warehouse_map_layouts");
        }
    }
}
