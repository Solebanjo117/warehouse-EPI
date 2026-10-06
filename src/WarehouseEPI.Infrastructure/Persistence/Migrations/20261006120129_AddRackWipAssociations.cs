using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRackWipAssociations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "location_rack_wip_associations",
                columns: table => new
                {
                    row_code = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    rack_number = table.Column<short>(type: "smallint", nullable: false),
                    wip_area_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_location_rack_wip_associations", x => new { x.row_code, x.rack_number });
                    table.CheckConstraint("ck_location_rack_wip_identity", "row_code ~ '^[A-Z]$' AND rack_number > 0");
                    table.ForeignKey(
                        name: "FK_location_rack_wip_associations_locations_wip_area_id",
                        column: x => x.wip_area_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_location_rack_wip_associations_wip_area_id",
                table: "location_rack_wip_associations",
                column: "wip_area_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "location_rack_wip_associations");
        }
    }
}
