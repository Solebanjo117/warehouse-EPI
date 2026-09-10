using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductionWorkOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements");

            migrationBuilder.CreateTable(
                name: "production_routes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_routes", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_routes_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_shifts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_shifts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "production_stages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_stages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "production_work_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    create_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    create_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    external_reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<short>(type: "smallint", nullable: false),
                    target_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    authorized_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_work_orders", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_work_orders_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_work_orders_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_work_orders_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_route_stages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    route_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_route_stages", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_route_stages_production_routes_route_id",
                        column: x => x.route_id,
                        principalTable: "production_routes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_route_stages_production_stages_stage_id",
                        column: x => x.stage_id,
                        principalTable: "production_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_work_order_stages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_work_order_stages", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_work_order_stages_production_stages_source_stage~",
                        column: x => x.source_stage_id,
                        principalTable: "production_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_work_order_stages_production_work_orders_work_or~",
                        column: x => x.work_order_id,
                        principalTable: "production_work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_order_stage_id = table.Column<Guid>(type: "uuid", nullable: true),
                    related_stage_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    responsible_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shift_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    good_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    rework_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    scrap_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    inventory_movement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_events_inventory_movements_inventory_movement_id",
                        column: x => x.inventory_movement_id,
                        principalTable: "inventory_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_events_production_shifts_shift_id",
                        column: x => x.shift_id,
                        principalTable: "production_shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_events_production_work_order_stages_related_stag~",
                        column: x => x.related_stage_id,
                        principalTable: "production_work_order_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_events_production_work_order_stages_work_order_s~",
                        column: x => x.work_order_stage_id,
                        principalTable: "production_work_order_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_events_production_work_orders_work_order_id",
                        column: x => x.work_order_id,
                        principalTable: "production_work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_events_users_responsible_user_id",
                        column: x => x.responsible_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements",
                sql: "(purpose = 'PRODUCTION_ISSUE' AND type IN ('ENTRY', 'EXIT', 'TRANSFER') AND operational_area_id IS NOT NULL) OR (purpose = 'GENERAL_EXIT' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (purpose = 'WIP_WAREHOUSE_RETURN' AND ((type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (type = 'TRANSFER' AND operational_area_id IS NOT NULL))) OR (purpose = 'WIP_CONSUMPTION' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL) OR (purpose = 'WIP_SUPPLIER_RETURN' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL AND NULLIF(BTRIM(reference), '') IS NOT NULL) OR (purpose = 'STANDARD' AND operational_area_id IS NULL) OR (purpose = 'CYCLE_COUNT_ADJUSTMENT' AND type = 'ADJUSTMENT' AND operational_area_id IS NULL) OR (purpose IN ('DOCUMENT_RECEIPT', 'PRODUCTION_RECEIPT') AND type = 'ENTRY' AND operational_area_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements",
                sql: "purpose IN ('STANDARD', 'GENERAL_EXIT', 'PRODUCTION_ISSUE', 'WIP_WAREHOUSE_RETURN', 'WIP_CONSUMPTION', 'WIP_SUPPLIER_RETURN', 'CYCLE_COUNT_ADJUSTMENT', 'DOCUMENT_RECEIPT', 'PRODUCTION_RECEIPT')");

            migrationBuilder.CreateIndex(
                name: "IX_production_events_inventory_movement_id",
                table: "production_events",
                column: "inventory_movement_id",
                unique: true,
                filter: "inventory_movement_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_production_events_operation_id",
                table: "production_events",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_events_related_stage_id",
                table: "production_events",
                column: "related_stage_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_events_responsible_user_id",
                table: "production_events",
                column: "responsible_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_events_shift_id",
                table: "production_events",
                column: "shift_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_events_work_order_id_recorded_at",
                table: "production_events",
                columns: new[] { "work_order_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_production_events_work_order_stage_id",
                table: "production_events",
                column: "work_order_stage_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_route_stages_route_id_sequence",
                table: "production_route_stages",
                columns: new[] { "route_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_route_stages_route_id_stage_id",
                table: "production_route_stages",
                columns: new[] { "route_id", "stage_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_route_stages_stage_id",
                table: "production_route_stages",
                column: "stage_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_routes_product_id",
                table: "production_routes",
                column: "product_id",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "IX_production_shifts_code",
                table: "production_shifts",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_stages_code",
                table: "production_stages",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_work_order_stages_source_stage_id",
                table: "production_work_order_stages",
                column: "source_stage_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_work_order_stages_work_order_id_sequence",
                table: "production_work_order_stages",
                columns: new[] { "work_order_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_work_order_stages_work_order_id_source_stage_id",
                table: "production_work_order_stages",
                columns: new[] { "work_order_id", "source_stage_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_work_orders_create_operation_id",
                table: "production_work_orders",
                column: "create_operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_work_orders_created_by_user_id",
                table: "production_work_orders",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_work_orders_number",
                table: "production_work_orders",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_work_orders_product_id",
                table: "production_work_orders",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_work_orders_status_due_date",
                table: "production_work_orders",
                columns: new[] { "status", "due_date" });

            migrationBuilder.CreateIndex(
                name: "IX_production_work_orders_unit_id",
                table: "production_work_orders",
                column: "unit_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_events");

            migrationBuilder.DropTable(
                name: "production_route_stages");

            migrationBuilder.DropTable(
                name: "production_shifts");

            migrationBuilder.DropTable(
                name: "production_work_order_stages");

            migrationBuilder.DropTable(
                name: "production_routes");

            migrationBuilder.DropTable(
                name: "production_stages");

            migrationBuilder.DropTable(
                name: "production_work_orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements",
                sql: "(purpose = 'PRODUCTION_ISSUE' AND type IN ('ENTRY', 'EXIT', 'TRANSFER') AND operational_area_id IS NOT NULL) OR (purpose = 'GENERAL_EXIT' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (purpose = 'WIP_WAREHOUSE_RETURN' AND ((type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (type = 'TRANSFER' AND operational_area_id IS NOT NULL))) OR (purpose = 'WIP_CONSUMPTION' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL) OR (purpose = 'WIP_SUPPLIER_RETURN' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL AND NULLIF(BTRIM(reference), '') IS NOT NULL) OR (purpose = 'STANDARD' AND operational_area_id IS NULL) OR (purpose = 'CYCLE_COUNT_ADJUSTMENT' AND type = 'ADJUSTMENT' AND operational_area_id IS NULL) OR (purpose = 'DOCUMENT_RECEIPT' AND type = 'ENTRY' AND operational_area_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements",
                sql: "purpose IN ('STANDARD', 'GENERAL_EXIT', 'PRODUCTION_ISSUE', 'WIP_WAREHOUSE_RETURN', 'WIP_CONSUMPTION', 'WIP_SUPPLIER_RETURN', 'CYCLE_COUNT_ADJUSTMENT', 'DOCUMENT_RECEIPT')");
        }
    }
}
