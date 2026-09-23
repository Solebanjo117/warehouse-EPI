using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase133ProductionSupplyRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "supply_priority",
                table: "production_work_orders",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "Normal");

            migrationBuilder.AddColumn<bool>(
                name: "uses_supply_requests",
                table: "production_work_orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "supply_request_line_id",
                table: "production_material_issue_links",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "production_supply_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_order_stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    destination_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_supply_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_supply_requests_locations_destination_location_id",
                        column: x => x.destination_location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_supply_requests_production_work_order_stages_wor~",
                        column: x => x.work_order_stage_id,
                        principalTable: "production_work_order_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_supply_requests_production_work_orders_work_orde~",
                        column: x => x.work_order_id,
                        principalTable: "production_work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_supply_request_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    supply_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    material_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<short>(type: "smallint", nullable: false),
                    required_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    cancelled_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_supply_request_lines", x => x.id);
                    table.CheckConstraint("ck_production_supply_request_line_quantities", "required_quantity > 0 AND cancelled_quantity >= 0 AND cancelled_quantity <= required_quantity");
                    table.ForeignKey(
                        name: "FK_production_supply_request_lines_production_order_material_p~",
                        column: x => x.material_plan_id,
                        principalTable: "production_order_material_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_supply_request_lines_production_supply_requests_~",
                        column: x => x.supply_request_id,
                        principalTable: "production_supply_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_production_supply_request_lines_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_supply_request_lines_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_supply_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    supply_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supply_request_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    responsible_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    inventory_movement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_supply_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_supply_events_inventory_movements_inventory_move~",
                        column: x => x.inventory_movement_id,
                        principalTable: "inventory_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_supply_events_production_supply_request_lines_su~",
                        column: x => x.supply_request_line_id,
                        principalTable: "production_supply_request_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_supply_events_production_supply_requests_supply_~",
                        column: x => x.supply_request_id,
                        principalTable: "production_supply_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_supply_events_users_responsible_user_id",
                        column: x => x.responsible_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_warehouse_reservations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    supply_request_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    released_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_warehouse_reservations", x => x.id);
                    table.CheckConstraint("ck_production_warehouse_reservation_quantities", "quantity > 0 AND released_quantity >= 0 AND released_quantity <= quantity");
                    table.ForeignKey(
                        name: "FK_production_warehouse_reservations_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_warehouse_reservations_product_lots_lot_id",
                        column: x => x.lot_id,
                        principalTable: "product_lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_warehouse_reservations_production_supply_request~",
                        column: x => x.supply_request_line_id,
                        principalTable: "production_supply_request_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issue_links_supply_request_line_id",
                table: "production_material_issue_links",
                column: "supply_request_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_events_inventory_movement_id",
                table: "production_supply_events",
                column: "inventory_movement_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_events_operation_id",
                table: "production_supply_events",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_events_responsible_user_id",
                table: "production_supply_events",
                column: "responsible_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_events_supply_request_id_recorded_at",
                table: "production_supply_events",
                columns: new[] { "supply_request_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_events_supply_request_line_id",
                table: "production_supply_events",
                column: "supply_request_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_request_lines_material_plan_id",
                table: "production_supply_request_lines",
                column: "material_plan_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_request_lines_product_id",
                table: "production_supply_request_lines",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_request_lines_supply_request_id",
                table: "production_supply_request_lines",
                column: "supply_request_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_request_lines_unit_id",
                table: "production_supply_request_lines",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_requests_destination_location_id",
                table: "production_supply_requests",
                column: "destination_location_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_requests_work_order_id_work_order_stage_i~",
                table: "production_supply_requests",
                columns: new[] { "work_order_id", "work_order_stage_id", "destination_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_requests_work_order_stage_id",
                table: "production_supply_requests",
                column: "work_order_stage_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_warehouse_reservations_location_id_lot_id",
                table: "production_warehouse_reservations",
                columns: new[] { "location_id", "lot_id" });

            migrationBuilder.CreateIndex(
                name: "IX_production_warehouse_reservations_lot_id",
                table: "production_warehouse_reservations",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_warehouse_reservations_supply_request_line_id_lo~",
                table: "production_warehouse_reservations",
                columns: new[] { "supply_request_line_id", "location_id", "lot_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_production_material_issue_links_production_supply_request_l~",
                table: "production_material_issue_links",
                column: "supply_request_line_id",
                principalTable: "production_supply_request_lines",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_production_material_issue_links_production_supply_request_l~",
                table: "production_material_issue_links");

            migrationBuilder.DropTable(
                name: "production_supply_events");

            migrationBuilder.DropTable(
                name: "production_warehouse_reservations");

            migrationBuilder.DropTable(
                name: "production_supply_request_lines");

            migrationBuilder.DropTable(
                name: "production_supply_requests");

            migrationBuilder.DropIndex(
                name: "IX_production_material_issue_links_supply_request_line_id",
                table: "production_material_issue_links");

            migrationBuilder.DropColumn(
                name: "supply_priority",
                table: "production_work_orders");

            migrationBuilder.DropColumn(
                name: "uses_supply_requests",
                table: "production_work_orders");

            migrationBuilder.DropColumn(
                name: "supply_request_line_id",
                table: "production_material_issue_links");
        }
    }
}
