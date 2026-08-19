using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWarehouseInteractiveMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "warehouse_map_layouts",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warehouse_map_layouts", x => x.id);
                    table.CheckConstraint("ck_warehouse_map_layout_singleton", "id = 1");
                    table.ForeignKey(
                        name: "FK_warehouse_map_layouts_users_updated_by_user_id",
                        column: x => x.updated_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "warehouse_map_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    previous_version = table.Column<int>(type: "integer", nullable: false),
                    new_version = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    changes_json = table.Column<string>(type: "jsonb", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authorized_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warehouse_map_revisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_warehouse_map_revisions_users_authorized_by_user_id",
                        column: x => x.authorized_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_warehouse_map_revisions_users_requested_by_user_id",
                        column: x => x.requested_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "warehouse_map_elements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    layout_id = table.Column<short>(type: "smallint", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    row_code = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: true),
                    rack_number = table.Column<short>(type: "smallint", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    x = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    y = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    width = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    height = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    rotation = table.Column<short>(type: "smallint", nullable: false),
                    z_index = table.Column<int>(type: "integer", nullable: false),
                    is_visible = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warehouse_map_elements", x => x.id);
                    table.CheckConstraint("ck_warehouse_map_element_geometry", "x >= 0 AND y >= 0 AND width > 0 AND height > 0 AND rotation IN (0, 90, 180, 270)");
                    table.CheckConstraint("ck_warehouse_map_element_identity", "(kind = 'RACK' AND row_code ~ '^[A-Z]$' AND rack_number > 0 AND location_id IS NULL) OR (kind = 'AREA' AND row_code IS NULL AND rack_number IS NULL AND location_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_warehouse_map_elements_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_warehouse_map_elements_warehouse_map_layouts_layout_id",
                        column: x => x.layout_id,
                        principalTable: "warehouse_map_layouts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_elements_layout_id_row_code_rack_number",
                table: "warehouse_map_elements",
                columns: new[] { "layout_id", "row_code", "rack_number" },
                unique: true,
                filter: "kind = 'RACK'");

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_elements_location_id",
                table: "warehouse_map_elements",
                column: "location_id",
                unique: true,
                filter: "kind = 'AREA'");

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_layouts_updated_by_user_id",
                table: "warehouse_map_layouts",
                column: "updated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_revisions_authorized_by_user_id",
                table: "warehouse_map_revisions",
                column: "authorized_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_revisions_operation_id",
                table: "warehouse_map_revisions",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_revisions_recorded_at",
                table: "warehouse_map_revisions",
                column: "recorded_at");

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_revisions_requested_by_user_id",
                table: "warehouse_map_revisions",
                column: "requested_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "warehouse_map_elements");

            migrationBuilder.DropTable(
                name: "warehouse_map_revisions");

            migrationBuilder.DropTable(
                name: "warehouse_map_layouts");
        }
    }
}
