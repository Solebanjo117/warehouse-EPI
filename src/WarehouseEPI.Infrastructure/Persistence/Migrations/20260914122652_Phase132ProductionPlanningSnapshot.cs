using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase132ProductionPlanningSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "wip_location_id",
                table: "production_order_material_plans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "wip_rack_number",
                table: "production_order_material_plans",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "wip_resolution_source",
                table: "production_order_material_plans",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "wip_row_code",
                table: "production_order_material_plans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "wip_target_code",
                table: "production_order_material_plans",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "wip_target_kind",
                table: "production_order_material_plans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "production_order_planning_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authorized_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    before_json = table.Column<string>(type: "text", nullable: false),
                    after_json = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_order_planning_revisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_order_planning_revisions_production_work_orders_~",
                        column: x => x.work_order_id,
                        principalTable: "production_work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_order_planning_revisions_users_authorized_by_use~",
                        column: x => x.authorized_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_order_material_plans_wip_location_id",
                table: "production_order_material_plans",
                column: "wip_location_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_production_order_material_plan_wip_shape",
                table: "production_order_material_plans",
                sql: "(wip_target_kind IS NULL AND wip_location_id IS NULL AND wip_row_code IS NULL AND wip_rack_number IS NULL AND wip_target_code IS NULL AND wip_resolution_source IS NULL) OR (wip_target_kind IN ('Area', 'Position') AND wip_location_id IS NOT NULL AND wip_row_code IS NULL AND wip_rack_number IS NULL AND wip_target_code IS NOT NULL AND wip_resolution_source IS NOT NULL) OR (wip_target_kind = 'Rack' AND wip_location_id IS NULL AND wip_row_code IS NOT NULL AND wip_rack_number IS NOT NULL AND wip_target_code IS NOT NULL AND wip_resolution_source IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_production_order_planning_revisions_authorized_by_user_id",
                table: "production_order_planning_revisions",
                column: "authorized_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_order_planning_revisions_operation_id",
                table: "production_order_planning_revisions",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_order_planning_revisions_work_order_id_recorded_~",
                table: "production_order_planning_revisions",
                columns: new[] { "work_order_id", "recorded_at" });

            migrationBuilder.AddForeignKey(
                name: "FK_production_order_material_plans_locations_wip_location_id",
                table: "production_order_material_plans",
                column: "wip_location_id",
                principalTable: "locations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_production_order_material_plans_locations_wip_location_id",
                table: "production_order_material_plans");

            migrationBuilder.DropTable(
                name: "production_order_planning_revisions");

            migrationBuilder.DropIndex(
                name: "IX_production_order_material_plans_wip_location_id",
                table: "production_order_material_plans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_production_order_material_plan_wip_shape",
                table: "production_order_material_plans");

            migrationBuilder.DropColumn(
                name: "wip_location_id",
                table: "production_order_material_plans");

            migrationBuilder.DropColumn(
                name: "wip_rack_number",
                table: "production_order_material_plans");

            migrationBuilder.DropColumn(
                name: "wip_resolution_source",
                table: "production_order_material_plans");

            migrationBuilder.DropColumn(
                name: "wip_row_code",
                table: "production_order_material_plans");

            migrationBuilder.DropColumn(
                name: "wip_target_code",
                table: "production_order_material_plans");

            migrationBuilder.DropColumn(
                name: "wip_target_kind",
                table: "production_order_material_plans");
        }
    }
}
