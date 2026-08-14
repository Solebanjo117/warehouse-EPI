using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventoryCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allows_negative_stock",
                table: "products");

            migrationBuilder.CreateTable(
                name: "inventory_movements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    responsible_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_movements", x => x.id);
                    table.CheckConstraint("ck_inventory_movements_type", "type IN ('ENTRY', 'EXIT', 'TRANSFER', 'ADJUSTMENT')");
                    table.ForeignKey(
                        name: "FK_inventory_movements_users_responsible_user_id",
                        column: x => x.responsible_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_lots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    normalized_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    expiration_date = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_lots", x => x.id);
                    table.CheckConstraint("ck_product_lots_normalized_number", "normalized_number = upper(btrim(normalized_number)) AND normalized_number <> ''");
                    table.ForeignKey(
                        name: "FK_product_lots_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_balances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_balances", x => x.id);
                    table.ForeignKey(
                        name: "FK_inventory_balances_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_balances_product_lots_lot_id",
                        column: x => x.lot_id,
                        principalTable: "product_lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_balances_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_movement_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<short>(type: "smallint", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    source_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    destination_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    previous_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    adjustment_delta = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_movement_lines", x => x.id);
                    table.CheckConstraint("ck_inventory_movement_lines_number", "line_number > 0");
                    table.ForeignKey(
                        name: "FK_inventory_movement_lines_inventory_movements_movement_id",
                        column: x => x.movement_id,
                        principalTable: "inventory_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_movement_lines_locations_destination_location_id",
                        column: x => x.destination_location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_movement_lines_locations_source_location_id",
                        column: x => x.source_location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_movement_lines_product_lots_lot_id",
                        column: x => x.lot_id,
                        principalTable: "product_lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_movement_lines_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_movement_lines_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_balance_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    movement_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    delta_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    previous_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    resulting_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_balance_changes", x => x.id);
                    table.CheckConstraint("ck_inventory_balance_changes_arithmetic", "previous_quantity + delta_quantity = resulting_quantity");
                    table.ForeignKey(
                        name: "FK_inventory_balance_changes_inventory_movement_lines_movement~",
                        column: x => x.movement_line_id,
                        principalTable: "inventory_movement_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_balance_changes_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_balance_changes_product_lots_lot_id",
                        column: x => x.lot_id,
                        principalTable: "product_lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_balance_changes_location_id",
                table: "inventory_balance_changes",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_balance_changes_lot_id",
                table: "inventory_balance_changes",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_balance_changes_movement_line_id",
                table: "inventory_balance_changes",
                column: "movement_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_balances_location_id",
                table: "inventory_balances",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_balances_lot_id",
                table: "inventory_balances",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_balances_product_id_location_id",
                table: "inventory_balances",
                columns: new[] { "product_id", "location_id" },
                unique: true,
                filter: "lot_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_balances_product_id_location_id_lot_id",
                table: "inventory_balances",
                columns: new[] { "product_id", "location_id", "lot_id" },
                unique: true,
                filter: "lot_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_balances_quantity",
                table: "inventory_balances",
                column: "quantity",
                filter: "quantity < 0");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movement_lines_destination_location_id",
                table: "inventory_movement_lines",
                column: "destination_location_id");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movement_lines_lot_id",
                table: "inventory_movement_lines",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movement_lines_movement_id_line_number",
                table: "inventory_movement_lines",
                columns: new[] { "movement_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movement_lines_product_id",
                table: "inventory_movement_lines",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movement_lines_source_location_id",
                table: "inventory_movement_lines",
                column: "source_location_id");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movement_lines_unit_id",
                table: "inventory_movement_lines",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_occurred_at",
                table: "inventory_movements",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_operation_id",
                table: "inventory_movements",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_responsible_user_id_occurred_at",
                table: "inventory_movements",
                columns: new[] { "responsible_user_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_product_lots_product_id_normalized_number",
                table: "product_lots",
                columns: new[] { "product_id", "normalized_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inventory_balance_changes");

            migrationBuilder.DropTable(
                name: "inventory_balances");

            migrationBuilder.DropTable(
                name: "inventory_movement_lines");

            migrationBuilder.DropTable(
                name: "inventory_movements");

            migrationBuilder.DropTable(
                name: "product_lots");

            migrationBuilder.AddColumn<bool>(
                name: "allows_negative_stock",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }
    }
}
