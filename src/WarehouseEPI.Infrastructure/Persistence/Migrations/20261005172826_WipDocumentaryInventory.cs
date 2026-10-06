using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WipDocumentaryInventory : Migration
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

            migrationBuilder.AlterColumn<Guid>(
                name: "inventory_movement_line_id",
                table: "production_material_operation_lines",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "wip_document_cutovers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Singleton = table.Column<int>(type: "integer", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResponsibleUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wip_document_cutovers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "wip_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovementLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    CutoverId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    WipLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResponsibleUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    IsOpening = table.Column<bool>(type: "boolean", nullable: false),
                    IsCancelled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wip_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wip_documents_inventory_movement_lines_MovementLineId",
                        column: x => x.MovementLineId,
                        principalTable: "inventory_movement_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wip_documents_locations_WipLocationId",
                        column: x => x.WipLocationId,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wip_documents_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wip_documents_users_ResponsibleUserId",
                        column: x => x.ResponsibleUserId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wip_document_applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "text", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueLinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    LotId = table.Column<Guid>(type: "uuid", nullable: true),
                    InventoryMovementLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversesApplicationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ResponsibleUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reference = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wip_document_applications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wip_document_applications_inventory_movement_lines_Inventor~",
                        column: x => x.InventoryMovementLineId,
                        principalTable: "inventory_movement_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wip_document_applications_wip_document_applications_Reverse~",
                        column: x => x.ReversesApplicationId,
                        principalTable: "wip_document_applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wip_document_applications_wip_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "wip_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wip_document_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wip_document_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wip_document_assignments_production_material_issue_links_Is~",
                        column: x => x.IssueLinkId,
                        principalTable: "production_material_issue_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wip_document_assignments_wip_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "wip_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wip_document_lots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    LotId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wip_document_lots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wip_document_lots_product_lots_LotId",
                        column: x => x.LotId,
                        principalTable: "product_lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wip_document_lots_wip_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "wip_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements",
                sql: "(purpose = 'PRODUCTION_ISSUE' AND type IN ('ENTRY', 'EXIT', 'TRANSFER') AND operational_area_id IS NOT NULL) OR (purpose = 'GENERAL_EXIT' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (purpose = 'WIP_WAREHOUSE_RETURN' AND ((type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (type = 'TRANSFER' AND operational_area_id IS NOT NULL))) OR (purpose = 'WIP_CONSUMPTION' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL) OR (purpose = 'WIP_SUPPLIER_RETURN' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL AND NULLIF(BTRIM(reference), '') IS NOT NULL) OR (purpose = 'STANDARD' AND operational_area_id IS NULL) OR (purpose = 'CYCLE_COUNT_ADJUSTMENT' AND type = 'ADJUSTMENT' AND operational_area_id IS NULL) OR (purpose IN ('DOCUMENT_RECEIPT', 'PRODUCTION_RECEIPT') AND type = 'ENTRY' AND operational_area_id IS NULL) OR (purpose = 'WIP_DOCUMENT_CUTOVER' AND type = 'ADJUSTMENT' AND operational_area_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements",
                sql: "purpose IN ('STANDARD', 'GENERAL_EXIT', 'PRODUCTION_ISSUE', 'WIP_WAREHOUSE_RETURN', 'WIP_CONSUMPTION', 'WIP_SUPPLIER_RETURN', 'CYCLE_COUNT_ADJUSTMENT', 'DOCUMENT_RECEIPT', 'PRODUCTION_RECEIPT', 'WIP_DOCUMENT_CUTOVER')");

            migrationBuilder.CreateIndex(
                name: "IX_wip_document_applications_DocumentId",
                table: "wip_document_applications",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_wip_document_applications_InventoryMovementLineId",
                table: "wip_document_applications",
                column: "InventoryMovementLineId");

            migrationBuilder.CreateIndex(
                name: "IX_wip_document_applications_OperationId",
                table: "wip_document_applications",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_wip_document_applications_ReversesApplicationId",
                table: "wip_document_applications",
                column: "ReversesApplicationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wip_document_assignments_DocumentId_IssueLinkId",
                table: "wip_document_assignments",
                columns: new[] { "DocumentId", "IssueLinkId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wip_document_assignments_IssueLinkId",
                table: "wip_document_assignments",
                column: "IssueLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_wip_document_cutovers_OperationId",
                table: "wip_document_cutovers",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wip_document_cutovers_Singleton",
                table: "wip_document_cutovers",
                column: "Singleton",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wip_document_lots_DocumentId",
                table: "wip_document_lots",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_wip_document_lots_LotId",
                table: "wip_document_lots",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_wip_documents_MovementLineId",
                table: "wip_documents",
                column: "MovementLineId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wip_documents_ProductId",
                table: "wip_documents",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_wip_documents_ResponsibleUserId",
                table: "wip_documents",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "IX_wip_documents_WipLocationId_ProductId_OccurredAt",
                table: "wip_documents",
                columns: new[] { "WipLocationId", "ProductId", "OccurredAt" });
            migrationBuilder.Sql("""
                CREATE FUNCTION enforce_wip_document_stock() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM wip_document_cutovers) THEN
                        PERFORM id FROM locations WHERE id = NEW.location_id FOR UPDATE;
                        IF NEW.quantity <> 0 AND EXISTS (SELECT 1 FROM locations WHERE id = NEW.location_id AND operational_role = 'WIP') THEN
                            RAISE EXCEPTION 'WIP is documentary and cannot hold inventory' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER wip_document_balance_guard BEFORE INSERT OR UPDATE ON inventory_balances
                    FOR EACH ROW EXECUTE FUNCTION enforce_wip_document_stock();
                CREATE TRIGGER wip_document_plate_guard BEFORE INSERT OR UPDATE ON pallet_plates
                    FOR EACH ROW EXECUTE FUNCTION enforce_wip_document_stock();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM wip_documents) OR EXISTS (SELECT 1 FROM wip_document_cutovers) THEN
                        RAISE EXCEPTION 'Documentary WIP contains audit data; restore a reviewed backup instead of dropping its history';
                    END IF;
                END $$;
                """);
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS wip_document_balance_guard ON inventory_balances;
                DROP TRIGGER IF EXISTS wip_document_plate_guard ON pallet_plates;
                DROP FUNCTION IF EXISTS enforce_wip_document_stock();
                """);
            migrationBuilder.DropTable(
                name: "wip_document_applications");

            migrationBuilder.DropTable(
                name: "wip_document_assignments");

            migrationBuilder.DropTable(
                name: "wip_document_cutovers");

            migrationBuilder.DropTable(
                name: "wip_document_lots");

            migrationBuilder.DropTable(
                name: "wip_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements");

            migrationBuilder.AlterColumn<Guid>(
                name: "inventory_movement_line_id",
                table: "production_material_operation_lines",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements",
                sql: "(purpose = 'PRODUCTION_ISSUE' AND type IN ('ENTRY', 'EXIT', 'TRANSFER') AND operational_area_id IS NOT NULL) OR (purpose = 'GENERAL_EXIT' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (purpose = 'WIP_WAREHOUSE_RETURN' AND ((type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (type = 'TRANSFER' AND operational_area_id IS NOT NULL))) OR (purpose = 'WIP_CONSUMPTION' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL) OR (purpose = 'WIP_SUPPLIER_RETURN' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL AND NULLIF(BTRIM(reference), '') IS NOT NULL) OR (purpose = 'STANDARD' AND operational_area_id IS NULL) OR (purpose = 'CYCLE_COUNT_ADJUSTMENT' AND type = 'ADJUSTMENT' AND operational_area_id IS NULL) OR (purpose IN ('DOCUMENT_RECEIPT', 'PRODUCTION_RECEIPT') AND type = 'ENTRY' AND operational_area_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements",
                sql: "purpose IN ('STANDARD', 'GENERAL_EXIT', 'PRODUCTION_ISSUE', 'WIP_WAREHOUSE_RETURN', 'WIP_CONSUMPTION', 'WIP_SUPPLIER_RETURN', 'CYCLE_COUNT_ADJUSTMENT', 'DOCUMENT_RECEIPT', 'PRODUCTION_RECEIPT')");
        }
    }
}
