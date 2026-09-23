using System;
using Microsoft.EntityFrameworkCore.Migrations;
using WarehouseEPI.Infrastructure.Labels;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OperationalPalletTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var preset = PalletLicensePlatePresetCatalog.Operational;
            var design = LabelDesignSerializer.Serialize(preset.Design);
            migrationBuilder.Sql($$"""
                INSERT INTO label_templates (id, code, kind, current_published_version_id, created_at, updated_at)
                VALUES ('{{preset.TemplateId}}', '{{preset.Code}}', 'PALLET_LICENSE_PLATE', NULL, '2026-09-17T00:00:00Z', '2026-09-17T00:00:00Z');
                INSERT INTO label_template_versions (id, template_id, version, name, size_preset, status, design_json, created_at, updated_at, published_at)
                VALUES ('{{preset.VersionId}}', '{{preset.TemplateId}}', 1, '{{preset.Name}}', '11X85_L', 'PUBLISHED', $plate${{design}}$plate$::jsonb, '2026-09-17T00:00:00Z', '2026-09-17T00:00:00Z', '2026-09-17T00:00:00Z');
                UPDATE label_templates SET current_published_version_id = '{{preset.VersionId}}' WHERE id = '{{preset.TemplateId}}';
                INSERT INTO label_template_events (id, template_id, template_version_id, type, reason, recorded_at)
                VALUES ('{{preset.EventId}}', '{{preset.TemplateId}}', '{{preset.VersionId}}', 'PUBLISHED', 'Placa operativa con identificador escaneable y estado actual.', '2026-09-17T00:00:00Z');
                """);
            migrationBuilder.AddColumn<string>(
                name: "PlatesJson",
                table: "production_supply_preparation_sources",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "PlateAllocationsJson",
                table: "production_material_issue_links",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "HasPlateDifference",
                table: "cycle_count_entries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PlateCountsJson",
                table: "cycle_count_entries",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "pallet_plates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginMovementId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    IsVoided = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pallet_plates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pallet_plates_inventory_movements_OriginMovementId",
                        column: x => x.OriginMovementId,
                        principalTable: "inventory_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pallet_plates_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pallet_plates_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pallet_plate_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlateId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovementId = table.Column<Guid>(type: "uuid", nullable: true),
                    MovementLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResponsibleUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReversesEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlateVersion = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Before = table.Column<string>(type: "text", nullable: false),
                    After = table.Column<string>(type: "text", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pallet_plate_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pallet_plate_events_inventory_movement_lines_MovementLineId",
                        column: x => x.MovementLineId,
                        principalTable: "inventory_movement_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pallet_plate_events_inventory_movements_MovementId",
                        column: x => x.MovementId,
                        principalTable: "inventory_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pallet_plate_events_pallet_plate_events_ReversesEventId",
                        column: x => x.ReversesEventId,
                        principalTable: "pallet_plate_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pallet_plate_events_pallet_plates_PlateId",
                        column: x => x.PlateId,
                        principalTable: "pallet_plates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pallet_plate_events_users_ResponsibleUserId",
                        column: x => x.ResponsibleUserId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pallet_plate_lots",
                columns: table => new
                {
                    PlateId = table.Column<Guid>(type: "uuid", nullable: false),
                    LotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pallet_plate_lots", x => new { x.PlateId, x.LotId });
                    table.ForeignKey(
                        name: "FK_pallet_plate_lots_pallet_plates_PlateId",
                        column: x => x.PlateId,
                        principalTable: "pallet_plates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pallet_plate_lots_product_lots_LotId",
                        column: x => x.LotId,
                        principalTable: "product_lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pallet_plate_events_MovementId",
                table: "pallet_plate_events",
                column: "MovementId");

            migrationBuilder.CreateIndex(
                name: "IX_pallet_plate_events_MovementLineId",
                table: "pallet_plate_events",
                column: "MovementLineId");

            migrationBuilder.CreateIndex(
                name: "IX_pallet_plate_events_OperationId",
                table: "pallet_plate_events",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_pallet_plate_events_PlateId_PlateVersion",
                table: "pallet_plate_events",
                columns: new[] { "PlateId", "PlateVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pallet_plate_events_ResponsibleUserId",
                table: "pallet_plate_events",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "IX_pallet_plate_events_ReversesEventId",
                table: "pallet_plate_events",
                column: "ReversesEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pallet_plate_lots_LotId",
                table: "pallet_plate_lots",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_pallet_plates_LocationId",
                table: "pallet_plates",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_pallet_plates_OriginMovementId",
                table: "pallet_plates",
                column: "OriginMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_pallet_plates_ProductId_LocationId",
                table: "pallet_plates",
                columns: new[] { "ProductId", "LocationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE label_templates SET current_published_version_id = NULL WHERE id = '73000000-0000-0000-0000-000000000001';
                DELETE FROM label_template_events WHERE id = '73000000-0000-0000-0000-000000000003';
                DELETE FROM label_template_versions WHERE id = '73000000-0000-0000-0000-000000000002';
                DELETE FROM label_templates WHERE id = '73000000-0000-0000-0000-000000000001';
                """);
            migrationBuilder.DropTable(
                name: "pallet_plate_events");

            migrationBuilder.DropTable(
                name: "pallet_plate_lots");

            migrationBuilder.DropTable(
                name: "pallet_plates");

            migrationBuilder.DropColumn(
                name: "PlatesJson",
                table: "production_supply_preparation_sources");

            migrationBuilder.DropColumn(
                name: "PlateAllocationsJson",
                table: "production_material_issue_links");

            migrationBuilder.DropColumn(
                name: "HasPlateDifference",
                table: "cycle_count_entries");

            migrationBuilder.DropColumn(
                name: "PlateCountsJson",
                table: "cycle_count_entries");
        }
    }
}
