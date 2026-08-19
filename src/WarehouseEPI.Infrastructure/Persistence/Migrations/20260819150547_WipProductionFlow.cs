using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WipProductionFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "operational_role",
                table: "locations",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "STORAGE");

            migrationBuilder.AddColumn<Guid>(
                name: "operational_area_id",
                table: "inventory_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "purpose",
                table: "inventory_movements",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "STANDARD");

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM locations
                        WHERE code IN ('WIP-2', 'WIP-3', 'WIP-4') AND kind <> 'AREA'
                    ) THEN
                        RAISE EXCEPTION 'WIP-2, WIP-3 y WIP-4 deben ser áreas especiales.';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM inventory_balances b
                        JOIN locations l ON l.id = b.location_id
                        WHERE l.code IN ('WIP-2', 'WIP-3', 'WIP-4') AND b.quantity <> 0
                    ) THEN
                        RAISE EXCEPTION 'No se puede clasificar WIP con saldos existentes.';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM product_location_assignments a
                        JOIN locations l ON l.id = a.location_id
                        WHERE l.code IN ('WIP-2', 'WIP-3', 'WIP-4') AND a.is_active
                    ) THEN
                        RAISE EXCEPTION 'No se puede clasificar WIP con asignaciones activas.';
                    END IF;
                END $$;

                INSERT INTO locations
                    (id, code, kind, operational_role, description, is_blocked, is_active, created_at, updated_at)
                VALUES
                    ('57495002-0000-4000-8000-000000000002', 'WIP-2', 'AREA', 'WIP', 'WIP 2 — consumo de producción', FALSE, TRUE, now(), now()),
                    ('57495003-0000-4000-8000-000000000003', 'WIP-3', 'AREA', 'WIP', 'WIP 3 — consumo de producción', FALSE, TRUE, now(), now()),
                    ('57495004-0000-4000-8000-000000000004', 'WIP-4', 'AREA', 'WIP', 'WIP 4 — consumo de producción', FALSE, TRUE, now(), now())
                ON CONFLICT (code) DO UPDATE
                SET operational_role = 'WIP',
                    description = COALESCE(locations.description, EXCLUDED.description),
                    updated_at = now();
                """);

            migrationBuilder.CreateTable(
                name: "wip_dispositions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    original_movement_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    responsible_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    inventory_movement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reverses_disposition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wip_dispositions", x => x.id);
                    table.CheckConstraint("ck_wip_dispositions_quantity", "quantity > 0");
                    table.CheckConstraint("ck_wip_dispositions_shape", "(type = 'WAREHOUSE_RETURN' AND destination_location_id IS NOT NULL AND inventory_movement_id IS NOT NULL) OR (type = 'SUPPLIER_RETURN' AND destination_location_id IS NULL AND inventory_movement_id IS NULL)");
                    table.CheckConstraint("ck_wip_dispositions_type", "type IN ('WAREHOUSE_RETURN', 'SUPPLIER_RETURN')");
                    table.ForeignKey(
                        name: "FK_wip_dispositions_inventory_movement_lines_original_movement~",
                        column: x => x.original_movement_line_id,
                        principalTable: "inventory_movement_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wip_dispositions_inventory_movements_inventory_movement_id",
                        column: x => x.inventory_movement_id,
                        principalTable: "inventory_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wip_dispositions_locations_destination_location_id",
                        column: x => x.destination_location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wip_dispositions_users_responsible_user_id",
                        column: x => x.responsible_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wip_dispositions_wip_dispositions_reverses_disposition_id",
                        column: x => x.reverses_disposition_id,
                        principalTable: "wip_dispositions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_locations_operational_role",
                table: "locations",
                sql: "operational_role IN ('STORAGE', 'WIP', 'OTHER')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_locations_wip_area",
                table: "locations",
                sql: "operational_role <> 'WIP' OR kind = 'AREA'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements",
                sql: "purpose IN ('STANDARD', 'GENERAL_EXIT', 'PRODUCTION_ISSUE', 'WIP_WAREHOUSE_RETURN')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements",
                sql: "(purpose = 'PRODUCTION_ISSUE' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL) OR (purpose = 'GENERAL_EXIT' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (purpose = 'WIP_WAREHOUSE_RETURN' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (purpose = 'STANDARD' AND operational_area_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_operational_area_id",
                table: "inventory_movements",
                column: "operational_area_id");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_purpose_occurred_at",
                table: "inventory_movements",
                columns: new[] { "purpose", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_wip_dispositions_destination_location_id",
                table: "wip_dispositions",
                column: "destination_location_id");

            migrationBuilder.CreateIndex(
                name: "IX_wip_dispositions_inventory_movement_id",
                table: "wip_dispositions",
                column: "inventory_movement_id",
                unique: true,
                filter: "inventory_movement_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_wip_dispositions_occurred_at",
                table: "wip_dispositions",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_wip_dispositions_operation_id",
                table: "wip_dispositions",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wip_dispositions_original_movement_line_id",
                table: "wip_dispositions",
                column: "original_movement_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_wip_dispositions_responsible_user_id",
                table: "wip_dispositions",
                column: "responsible_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_wip_dispositions_reverses_disposition_id",
                table: "wip_dispositions",
                column: "reverses_disposition_id",
                unique: true,
                filter: "reverses_disposition_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_inventory_movements_locations_operational_area_id",
                table: "inventory_movements",
                column: "operational_area_id",
                principalTable: "locations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_inventory_movements_locations_operational_area_id",
                table: "inventory_movements");

            migrationBuilder.DropTable(
                name: "wip_dispositions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_locations_operational_role",
                table: "locations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_locations_wip_area",
                table: "locations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements");

            migrationBuilder.DropIndex(
                name: "IX_inventory_movements_operational_area_id",
                table: "inventory_movements");

            migrationBuilder.DropIndex(
                name: "IX_inventory_movements_purpose_occurred_at",
                table: "inventory_movements");

            migrationBuilder.DropColumn(
                name: "operational_role",
                table: "locations");

            migrationBuilder.DropColumn(
                name: "operational_area_id",
                table: "inventory_movements");

            migrationBuilder.DropColumn(
                name: "purpose",
                table: "inventory_movements");
        }
    }
}
