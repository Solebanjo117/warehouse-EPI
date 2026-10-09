using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMaterialIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "material_incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitId = table.Column<short>(type: "smallint", nullable: false),
                    DetectionLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlateId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArrivalLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Difference = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Snapshot = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReportedById = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_material_incidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_material_incidents_inventory_movement_lines_ArrivalLineId",
                        column: x => x.ArrivalLineId,
                        principalTable: "inventory_movement_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_incidents_locations_DetectionLocationId",
                        column: x => x.DetectionLocationId,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_incidents_pallet_plates_PlateId",
                        column: x => x.PlateId,
                        principalTable: "pallet_plates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_incidents_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_incidents_units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_incidents_users_ReportedById",
                        column: x => x.ReportedById,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "material_incident_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CorrectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResponsibleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_material_incident_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_material_incident_events_inventory_movement_corrections_Cor~",
                        column: x => x.CorrectionId,
                        principalTable: "inventory_movement_corrections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_incident_events_material_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "material_incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_incident_events_users_ResponsibleId",
                        column: x => x.ResponsibleId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "material_incident_photos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_material_incident_photos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_material_incident_photos_material_incident_events_EventId",
                        column: x => x.EventId,
                        principalTable: "material_incident_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_incident_photos_material_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "material_incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_material_incident_events_CorrectionId",
                table: "material_incident_events",
                column: "CorrectionId");

            migrationBuilder.CreateIndex(
                name: "IX_material_incident_events_IncidentId_Version",
                table: "material_incident_events",
                columns: new[] { "IncidentId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_material_incident_events_OperationId",
                table: "material_incident_events",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_material_incident_events_ResponsibleId",
                table: "material_incident_events",
                column: "ResponsibleId");

            migrationBuilder.CreateIndex(
                name: "IX_material_incident_photos_EventId",
                table: "material_incident_photos",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_material_incident_photos_IncidentId_Sha256",
                table: "material_incident_photos",
                columns: new[] { "IncidentId", "Sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_material_incidents_ArrivalLineId",
                table: "material_incidents",
                column: "ArrivalLineId");

            migrationBuilder.CreateIndex(
                name: "IX_material_incidents_DetectionLocationId",
                table: "material_incidents",
                column: "DetectionLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_material_incidents_PlateId",
                table: "material_incidents",
                column: "PlateId");

            migrationBuilder.CreateIndex(
                name: "IX_material_incidents_ProductId_DetectionLocationId_Status",
                table: "material_incidents",
                columns: new[] { "ProductId", "DetectionLocationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_material_incidents_ReportedById",
                table: "material_incidents",
                column: "ReportedById");

            migrationBuilder.CreateIndex(
                name: "IX_material_incidents_Status_ReportedAt_Id",
                table: "material_incidents",
                columns: new[] { "Status", "ReportedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_material_incidents_UnitId",
                table: "material_incidents",
                column: "UnitId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "material_incident_photos");

            migrationBuilder.DropTable(
                name: "material_incident_events");

            migrationBuilder.DropTable(
                name: "material_incidents");
        }
    }
}
