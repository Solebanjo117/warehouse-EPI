using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProcessRowAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_production_process_wip_targets_production_stage_id_row_code~",
                table: "production_process_wip_targets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_production_process_wip_targets_shape",
                table: "production_process_wip_targets");

            migrationBuilder.CreateIndex(
                name: "IX_production_process_wip_targets_production_stage_id_row_code",
                table: "production_process_wip_targets",
                columns: new[] { "production_stage_id", "row_code" },
                unique: true,
                filter: "row_code IS NOT NULL AND rack_number IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_production_process_wip_targets_production_stage_id_row_code~",
                table: "production_process_wip_targets",
                columns: new[] { "production_stage_id", "row_code", "rack_number" },
                unique: true,
                filter: "rack_number IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_production_process_wip_targets_shape",
                table: "production_process_wip_targets",
                sql: "(location_id IS NOT NULL AND row_code IS NULL AND rack_number IS NULL) OR (location_id IS NULL AND row_code IS NOT NULL AND rack_number IS NULL) OR (location_id IS NULL AND row_code IS NOT NULL AND rack_number IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_production_process_wip_targets_production_stage_id_row_code",
                table: "production_process_wip_targets");

            migrationBuilder.DropIndex(
                name: "IX_production_process_wip_targets_production_stage_id_row_code~",
                table: "production_process_wip_targets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_production_process_wip_targets_shape",
                table: "production_process_wip_targets");

            migrationBuilder.CreateIndex(
                name: "IX_production_process_wip_targets_production_stage_id_row_code~",
                table: "production_process_wip_targets",
                columns: new[] { "production_stage_id", "row_code", "rack_number" },
                unique: true,
                filter: "row_code IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_production_process_wip_targets_shape",
                table: "production_process_wip_targets",
                sql: "(location_id IS NOT NULL AND row_code IS NULL AND rack_number IS NULL) OR (location_id IS NULL AND row_code IS NOT NULL AND rack_number IS NOT NULL)");
        }
    }
}
