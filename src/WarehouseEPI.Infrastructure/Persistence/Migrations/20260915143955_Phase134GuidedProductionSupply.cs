using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase134GuidedProductionSupply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "destination_code",
                table: "production_supply_request_lines",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "destination_location_id",
                table: "production_supply_request_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "reopened_quantity",
                table: "production_supply_request_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "return_effect",
                table: "production_material_operations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "inventory_movement_line_id",
                table: "production_material_issue_links",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<decimal>(
                name: "cancelled_quantity",
                table: "production_material_issue_links",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "product_id",
                table: "production_material_issue_links",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<decimal>(
                name: "quantity",
                table: "production_material_issue_links",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "production_material_issue_links",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "wip_location_id",
                table: "production_material_issue_links",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.Sql(
                """
                UPDATE production_material_issue_links AS issue
                SET product_id = line.product_id,
                    wip_location_id = COALESCE(line.destination_location_id, issue.wip_location_id),
                    quantity = line.quantity,
                    source = 'Transfer'
                FROM inventory_movement_lines AS line
                WHERE issue.inventory_movement_line_id = line.id;

                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM production_material_issue_links
                        WHERE inventory_movement_line_id IS NULL
                           OR product_id = '00000000-0000-0000-0000-000000000000'
                           OR wip_location_id = '00000000-0000-0000-0000-000000000000'
                           OR quantity <= 0
                           OR source <> 'Transfer') THEN
                        RAISE EXCEPTION 'P4 cannot backfill a historical production material issue from its movement line.';
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateTable(
                name: "production_material_issue_lots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_material_issue_lots", x => x.id);
                    table.CheckConstraint("ck_production_material_issue_lot_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_production_material_issue_lots_product_lots_lot_id",
                        column: x => x.lot_id,
                        principalTable: "product_lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_material_issue_lots_production_material_issue_li~",
                        column: x => x.issue_link_id,
                        principalTable: "production_material_issue_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "production_supply_preparations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    supply_request_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    responsible_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_supply_preparations", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_supply_preparations_locations_destination_locati~",
                        column: x => x.destination_location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_supply_preparations_production_supply_request_li~",
                        column: x => x.supply_request_line_id,
                        principalTable: "production_supply_request_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_supply_preparations_users_responsible_user_id",
                        column: x => x.responsible_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_supply_confirmations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    supply_request_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    preparation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    responsible_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_supply_confirmations", x => x.id);
                    table.CheckConstraint("ck_production_supply_confirmation_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_production_supply_confirmations_locations_destination_locat~",
                        column: x => x.destination_location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_supply_confirmations_production_supply_preparati~",
                        column: x => x.preparation_id,
                        principalTable: "production_supply_preparations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_supply_confirmations_production_supply_request_l~",
                        column: x => x.supply_request_line_id,
                        principalTable: "production_supply_request_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_supply_confirmations_users_responsible_user_id",
                        column: x => x.responsible_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO production_material_issue_lots (id, issue_link_id, lot_id, quantity)
                SELECT md5(issue.id::text || ':' || change.lot_id::text)::uuid,
                       issue.id,
                       change.lot_id,
                       SUM(change.delta_quantity)
                FROM production_material_issue_links AS issue
                JOIN inventory_balance_changes AS change
                  ON change.movement_line_id = issue.inventory_movement_line_id
                 AND change.location_id = issue.wip_location_id
                 AND change.lot_id IS NOT NULL
                 AND change.delta_quantity > 0
                GROUP BY issue.id, change.lot_id;

                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM production_material_issue_links AS issue
                        LEFT JOIN production_material_issue_lots AS lot ON lot.issue_link_id = issue.id
                        GROUP BY issue.id, issue.quantity
                        HAVING COALESCE(SUM(lot.quantity), 0) <> issue.quantity) THEN
                        RAISE EXCEPTION 'P4 cannot reconcile historical production material issue lots with the confirmed movement.';
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateTable(
                name: "production_supply_preparation_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    preparation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_supply_preparation_sources", x => x.id);
                    table.CheckConstraint("ck_production_supply_preparation_source_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_production_supply_preparation_sources_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_supply_preparation_sources_production_supply_pre~",
                        column: x => x.preparation_id,
                        principalTable: "production_supply_preparations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "production_supply_confirmation_issues",
                columns: table => new
                {
                    confirmation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_link_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_supply_confirmation_issues", x => new { x.confirmation_id, x.issue_link_id });
                    table.ForeignKey(
                        name: "FK_production_supply_confirmation_issues_production_material_i~",
                        column: x => x.issue_link_id,
                        principalTable: "production_material_issue_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_supply_confirmation_issues_production_supply_con~",
                        column: x => x.confirmation_id,
                        principalTable: "production_supply_confirmations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "production_supply_confirmation_movements",
                columns: table => new
                {
                    confirmation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_movement_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_supply_confirmation_movements", x => new { x.confirmation_id, x.inventory_movement_id });
                    table.ForeignKey(
                        name: "FK_production_supply_confirmation_movements_inventory_movement~",
                        column: x => x.inventory_movement_id,
                        principalTable: "inventory_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_supply_confirmation_movements_production_supply_~",
                        column: x => x.confirmation_id,
                        principalTable: "production_supply_confirmations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_request_lines_destination_location_id",
                table: "production_supply_request_lines",
                column: "destination_location_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issue_links_product_id",
                table: "production_material_issue_links",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issue_links_wip_location_id",
                table: "production_material_issue_links",
                column: "wip_location_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_production_material_issue_link_cancelled",
                table: "production_material_issue_links",
                sql: "cancelled_quantity >= 0 AND cancelled_quantity <= quantity");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issue_lots_issue_link_id_lot_id",
                table: "production_material_issue_lots",
                columns: new[] { "issue_link_id", "lot_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issue_lots_lot_id",
                table: "production_material_issue_lots",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_confirmation_issues_issue_link_id",
                table: "production_supply_confirmation_issues",
                column: "issue_link_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_confirmation_movements_inventory_movement~",
                table: "production_supply_confirmation_movements",
                column: "inventory_movement_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_confirmations_destination_location_id",
                table: "production_supply_confirmations",
                column: "destination_location_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_confirmations_operation_id",
                table: "production_supply_confirmations",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_confirmations_preparation_id",
                table: "production_supply_confirmations",
                column: "preparation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_confirmations_responsible_user_id",
                table: "production_supply_confirmations",
                column: "responsible_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_confirmations_supply_request_line_id",
                table: "production_supply_confirmations",
                column: "supply_request_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_preparation_sources_location_id",
                table: "production_supply_preparation_sources",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_preparation_sources_preparation_id_kind_l~",
                table: "production_supply_preparation_sources",
                columns: new[] { "preparation_id", "kind", "location_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_preparations_destination_location_id",
                table: "production_supply_preparations",
                column: "destination_location_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_preparations_operation_id",
                table: "production_supply_preparations",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_preparations_responsible_user_id",
                table: "production_supply_preparations",
                column: "responsible_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_preparations_supply_request_line_id",
                table: "production_supply_preparations",
                column: "supply_request_line_id",
                unique: true,
                filter: "status = 'Open'");

            migrationBuilder.AddForeignKey(
                name: "FK_production_material_issue_links_locations_wip_location_id",
                table: "production_material_issue_links",
                column: "wip_location_id",
                principalTable: "locations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_production_material_issue_links_products_product_id",
                table: "production_material_issue_links",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_production_supply_request_lines_locations_destination_locat~",
                table: "production_supply_request_lines",
                column: "destination_location_id",
                principalTable: "locations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM production_material_issue_links
                        WHERE source <> 'Transfer'
                           OR inventory_movement_line_id IS NULL) THEN
                        RAISE EXCEPTION 'P4 cannot be downgraded while WIP assignments without a movement exist.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_production_material_issue_links_locations_wip_location_id",
                table: "production_material_issue_links");

            migrationBuilder.DropForeignKey(
                name: "FK_production_material_issue_links_products_product_id",
                table: "production_material_issue_links");

            migrationBuilder.DropForeignKey(
                name: "FK_production_supply_request_lines_locations_destination_locat~",
                table: "production_supply_request_lines");

            migrationBuilder.DropTable(
                name: "production_material_issue_lots");

            migrationBuilder.DropTable(
                name: "production_supply_confirmation_issues");

            migrationBuilder.DropTable(
                name: "production_supply_confirmation_movements");

            migrationBuilder.DropTable(
                name: "production_supply_preparation_sources");

            migrationBuilder.DropTable(
                name: "production_supply_confirmations");

            migrationBuilder.DropTable(
                name: "production_supply_preparations");

            migrationBuilder.DropIndex(
                name: "IX_production_supply_request_lines_destination_location_id",
                table: "production_supply_request_lines");

            migrationBuilder.DropIndex(
                name: "IX_production_material_issue_links_product_id",
                table: "production_material_issue_links");

            migrationBuilder.DropIndex(
                name: "IX_production_material_issue_links_wip_location_id",
                table: "production_material_issue_links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_production_material_issue_link_cancelled",
                table: "production_material_issue_links");

            migrationBuilder.DropColumn(
                name: "destination_code",
                table: "production_supply_request_lines");

            migrationBuilder.DropColumn(
                name: "destination_location_id",
                table: "production_supply_request_lines");

            migrationBuilder.DropColumn(
                name: "reopened_quantity",
                table: "production_supply_request_lines");

            migrationBuilder.DropColumn(
                name: "return_effect",
                table: "production_material_operations");

            migrationBuilder.DropColumn(
                name: "cancelled_quantity",
                table: "production_material_issue_links");

            migrationBuilder.DropColumn(
                name: "product_id",
                table: "production_material_issue_links");

            migrationBuilder.DropColumn(
                name: "quantity",
                table: "production_material_issue_links");

            migrationBuilder.DropColumn(
                name: "source",
                table: "production_material_issue_links");

            migrationBuilder.DropColumn(
                name: "wip_location_id",
                table: "production_material_issue_links");

            migrationBuilder.AlterColumn<Guid>(
                name: "inventory_movement_line_id",
                table: "production_material_issue_links",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
