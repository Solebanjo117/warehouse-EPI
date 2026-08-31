using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BlockCReceivingAndTrace : Migration
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
                name: "receiving_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    number = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    normalized_number = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    origin = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    normalized_origin = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    document_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    opened_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receiving_documents", x => x.id);
                    table.CheckConstraint("ck_receiving_documents_number", "length(btrim(number)) > 0 AND normalized_number = upper(btrim(number))");
                    table.CheckConstraint("ck_receiving_documents_origin", "length(btrim(origin)) > 0 AND normalized_origin = upper(btrim(origin))");
                    table.CheckConstraint("ck_receiving_documents_status", "status IN ('OPEN','PARTIALLY_RECEIVED','COMPLETED','CLOSED_WITH_DIFFERENCES','CANCELLED')");
                    table.CheckConstraint("ck_receiving_documents_terminal_shape", "(status = 'COMPLETED' AND completed_at IS NOT NULL AND closed_at IS NULL AND cancelled_at IS NULL) OR (status = 'CLOSED_WITH_DIFFERENCES' AND closed_at IS NOT NULL AND close_reason IS NOT NULL AND cancelled_at IS NULL) OR (status = 'CANCELLED' AND cancelled_at IS NOT NULL AND cancel_reason IS NOT NULL AND closed_at IS NULL) OR (status IN ('OPEN','PARTIALLY_RECEIVED') AND completed_at IS NULL AND closed_at IS NULL AND cancelled_at IS NULL)");
                    table.CheckConstraint("ck_receiving_documents_type", "type IN ('PURCHASE_ORDER','DELIVERY_NOTE','PACKING_LIST','PRODUCTION_ORDER','OTHER')");
                    table.ForeignKey(
                        name: "FK_receiving_documents_users_cancelled_by_user_id",
                        column: x => x.cancelled_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_receiving_documents_users_closed_by_user_id",
                        column: x => x.closed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_receiving_documents_users_opened_by_user_id",
                        column: x => x.opened_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receiving_confirmations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    receiving_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    responsible_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    difference_acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    difference_notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receiving_confirmations", x => x.id);
                    table.ForeignKey(
                        name: "FK_receiving_confirmations_inventory_movements_inventory_movem~",
                        column: x => x.inventory_movement_id,
                        principalTable: "inventory_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_receiving_confirmations_receiving_documents_receiving_docum~",
                        column: x => x.receiving_document_id,
                        principalTable: "receiving_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_receiving_confirmations_users_responsible_user_id",
                        column: x => x.responsible_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receiving_document_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    receiving_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receiving_document_events", x => x.id);
                    table.CheckConstraint("ck_receiving_document_events_type", "type IN ('OPENED','RECEIPT_CONFIRMED','AUTOMATICALLY_COMPLETED','CLOSED_WITH_DIFFERENCES','CANCELLED','RECEIPT_CORRECTED','REOPENED_AFTER_CORRECTION')");
                    table.ForeignKey(
                        name: "FK_receiving_document_events_receiving_documents_receiving_doc~",
                        column: x => x.receiving_document_id,
                        principalTable: "receiving_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_receiving_document_events_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receiving_document_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    receiving_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<short>(type: "smallint", nullable: false),
                    expected_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receiving_document_lines", x => x.id);
                    table.CheckConstraint("ck_receiving_document_lines_number", "line_number > 0");
                    table.CheckConstraint("ck_receiving_document_lines_quantity", "expected_quantity > 0");
                    table.ForeignKey(
                        name: "FK_receiving_document_lines_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_receiving_document_lines_receiving_documents_receiving_docu~",
                        column: x => x.receiving_document_id,
                        principalTable: "receiving_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_receiving_document_lines_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receiving_confirmation_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    receiving_confirmation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receiving_document_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    inventory_movement_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_lot_reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receiving_confirmation_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_receiving_confirmation_lines_inventory_movement_lines_inven~",
                        column: x => x.inventory_movement_line_id,
                        principalTable: "inventory_movement_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_receiving_confirmation_lines_receiving_confirmations_receiv~",
                        column: x => x.receiving_confirmation_id,
                        principalTable: "receiving_confirmations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_receiving_confirmation_lines_receiving_document_lines_recei~",
                        column: x => x.receiving_document_line_id,
                        principalTable: "receiving_document_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements",
                sql: "(purpose = 'PRODUCTION_ISSUE' AND type IN ('ENTRY', 'EXIT', 'TRANSFER') AND operational_area_id IS NOT NULL) OR (purpose = 'GENERAL_EXIT' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (purpose = 'WIP_WAREHOUSE_RETURN' AND ((type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (type = 'TRANSFER' AND operational_area_id IS NOT NULL))) OR (purpose = 'WIP_CONSUMPTION' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL) OR (purpose = 'WIP_SUPPLIER_RETURN' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL AND NULLIF(BTRIM(reference), '') IS NOT NULL) OR (purpose = 'STANDARD' AND operational_area_id IS NULL) OR (purpose = 'CYCLE_COUNT_ADJUSTMENT' AND type = 'ADJUSTMENT' AND operational_area_id IS NULL) OR (purpose = 'DOCUMENT_RECEIPT' AND type = 'ENTRY' AND operational_area_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements",
                sql: "purpose IN ('STANDARD', 'GENERAL_EXIT', 'PRODUCTION_ISSUE', 'WIP_WAREHOUSE_RETURN', 'WIP_CONSUMPTION', 'WIP_SUPPLIER_RETURN', 'CYCLE_COUNT_ADJUSTMENT', 'DOCUMENT_RECEIPT')");

            migrationBuilder.CreateIndex(
                name: "IX_receiving_confirmation_lines_external_lot_reference",
                table: "receiving_confirmation_lines",
                column: "external_lot_reference",
                filter: "external_lot_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_receiving_confirmation_lines_inventory_movement_line_id",
                table: "receiving_confirmation_lines",
                column: "inventory_movement_line_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receiving_confirmation_lines_receiving_confirmation_id",
                table: "receiving_confirmation_lines",
                column: "receiving_confirmation_id");

            migrationBuilder.CreateIndex(
                name: "IX_receiving_confirmation_lines_receiving_document_line_id",
                table: "receiving_confirmation_lines",
                column: "receiving_document_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_receiving_confirmations_inventory_movement_id",
                table: "receiving_confirmations",
                column: "inventory_movement_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receiving_confirmations_operation_id",
                table: "receiving_confirmations",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receiving_confirmations_receiving_document_id_occurred_at",
                table: "receiving_confirmations",
                columns: new[] { "receiving_document_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_receiving_confirmations_responsible_user_id",
                table: "receiving_confirmations",
                column: "responsible_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_receiving_document_events_actor_user_id",
                table: "receiving_document_events",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_receiving_document_events_operation_id",
                table: "receiving_document_events",
                column: "operation_id",
                unique: true,
                filter: "operation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_receiving_document_events_receiving_document_id_recorded_at",
                table: "receiving_document_events",
                columns: new[] { "receiving_document_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_receiving_document_lines_product_id",
                table: "receiving_document_lines",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_receiving_document_lines_receiving_document_id_line_number",
                table: "receiving_document_lines",
                columns: new[] { "receiving_document_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receiving_document_lines_receiving_document_id_product_id",
                table: "receiving_document_lines",
                columns: new[] { "receiving_document_id", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receiving_document_lines_unit_id",
                table: "receiving_document_lines",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "IX_receiving_documents_cancelled_by_user_id",
                table: "receiving_documents",
                column: "cancelled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_receiving_documents_closed_by_user_id",
                table: "receiving_documents",
                column: "closed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_receiving_documents_opened_by_user_id",
                table: "receiving_documents",
                column: "opened_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_receiving_documents_operation_id",
                table: "receiving_documents",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receiving_documents_status_opened_at",
                table: "receiving_documents",
                columns: new[] { "status", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "IX_receiving_documents_type_normalized_number_normalized_origin",
                table: "receiving_documents",
                columns: new[] { "type", "normalized_number", "normalized_origin" },
                unique: true,
                filter: "status <> 'CANCELLED'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "receiving_confirmation_lines");

            migrationBuilder.DropTable(
                name: "receiving_document_events");

            migrationBuilder.DropTable(
                name: "receiving_confirmations");

            migrationBuilder.DropTable(
                name: "receiving_document_lines");

            migrationBuilder.DropTable(
                name: "receiving_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements",
                sql: "(purpose = 'PRODUCTION_ISSUE' AND type IN ('ENTRY', 'EXIT', 'TRANSFER') AND operational_area_id IS NOT NULL) OR (purpose = 'GENERAL_EXIT' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (purpose = 'WIP_WAREHOUSE_RETURN' AND ((type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (type = 'TRANSFER' AND operational_area_id IS NOT NULL))) OR (purpose = 'WIP_CONSUMPTION' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL) OR (purpose = 'WIP_SUPPLIER_RETURN' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL AND NULLIF(BTRIM(reference), '') IS NOT NULL) OR (purpose = 'STANDARD' AND operational_area_id IS NULL) OR (purpose = 'CYCLE_COUNT_ADJUSTMENT' AND type = 'ADJUSTMENT' AND operational_area_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements",
                sql: "purpose IN ('STANDARD', 'GENERAL_EXIT', 'PRODUCTION_ISSUE', 'WIP_WAREHOUSE_RETURN', 'WIP_CONSUMPTION', 'WIP_SUPPLIER_RETURN', 'CYCLE_COUNT_ADJUSTMENT')");
        }
    }
}
