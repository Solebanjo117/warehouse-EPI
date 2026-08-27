using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WarehouseDbContext))]
[Migration("20260825093000_AddWarehouseMapReferenceImages")]
public sealed class AddWarehouseMapReferenceImages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "warehouse_map_reference_images",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                layout_id = table.Column<short>(type: "smallint", nullable: false),
                original_file_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                stored_file_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                content_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                pixel_width = table.Column<int>(type: "integer", nullable: false),
                pixel_height = table.Column<int>(type: "integer", nullable: false),
                x = table.Column<decimal>(type: "numeric(9,3)", precision: 9, scale: 3, nullable: false),
                y = table.Column<decimal>(type: "numeric(9,3)", precision: 9, scale: 3, nullable: false),
                width = table.Column<decimal>(type: "numeric(9,3)", precision: 9, scale: 3, nullable: false),
                height = table.Column<decimal>(type: "numeric(9,3)", precision: 9, scale: 3, nullable: false),
                rotation = table.Column<short>(type: "smallint", nullable: false),
                opacity = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: false, defaultValue: 0.35m),
                is_locked = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                is_archived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                calibration_a_x = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: true),
                calibration_a_y = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: true),
                calibration_b_x = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: true),
                calibration_b_y = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: true),
                calibration_distance_inches = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_warehouse_map_reference_images", x => x.id);
                table.CheckConstraint("ck_warehouse_map_reference_calibration", "(calibration_a_x IS NULL AND calibration_a_y IS NULL AND calibration_b_x IS NULL AND calibration_b_y IS NULL AND calibration_distance_inches IS NULL) OR (calibration_a_x BETWEEN 0 AND 1 AND calibration_a_y BETWEEN 0 AND 1 AND calibration_b_x BETWEEN 0 AND 1 AND calibration_b_y BETWEEN 0 AND 1 AND calibration_distance_inches > 0)");
                table.CheckConstraint("ck_warehouse_map_reference_file", "content_type IN ('image/png', 'image/jpeg', 'image/webp') AND pixel_width BETWEEN 1 AND 4096 AND pixel_height BETWEEN 1 AND 4096");
                table.CheckConstraint("ck_warehouse_map_reference_geometry", "x >= 0 AND y >= 0 AND width > 0 AND height > 0 AND rotation IN (0, 90, 180, 270) AND opacity BETWEEN 0.05 AND 1");
                table.ForeignKey("fk_warehouse_map_reference_images_users_created_by_user_id", x => x.created_by_user_id, "users", "id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("fk_warehouse_map_reference_images_warehouse_map_layouts_layout_id", x => x.layout_id, "warehouse_map_layouts", "id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("ix_warehouse_map_reference_images_created_by_user_id", "warehouse_map_reference_images", "created_by_user_id");
        migrationBuilder.CreateIndex("ix_warehouse_map_reference_images_layout_id_is_archived", "warehouse_map_reference_images", new[] { "layout_id", "is_archived" }, unique: true, filter: "is_archived = false");
        migrationBuilder.CreateIndex("ix_warehouse_map_reference_images_layout_id_sha256", "warehouse_map_reference_images", new[] { "layout_id", "sha256" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "warehouse_map_reference_images");
}
