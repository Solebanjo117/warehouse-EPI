using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CycleCountPlanAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "created_by_user_id",
                table: "cycle_count_plans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "updated_by_user_id",
                table: "cycle_count_plans",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cycle_count_plan_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_count_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    responsible_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cycle_count_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    previous_frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    new_frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    previous_anchor_date = table.Column<DateOnly>(type: "date", nullable: true),
                    new_anchor_date = table.Column<DateOnly>(type: "date", nullable: true),
                    previous_next_due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    new_next_due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    previous_is_active = table.Column<bool>(type: "boolean", nullable: true),
                    new_is_active = table.Column<bool>(type: "boolean", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cycle_count_plan_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_cycle_count_plan_events_cycle_count_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "cycle_count_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_plan_events_cycle_count_locations_cycle_count_l~",
                        column: x => x.cycle_count_location_id,
                        principalTable: "cycle_count_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_plan_events_cycle_count_plans_cycle_count_plan_~",
                        column: x => x.cycle_count_plan_id,
                        principalTable: "cycle_count_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_plan_events_users_responsible_user_id",
                        column: x => x.responsible_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_plans_created_by_user_id",
                table: "cycle_count_plans",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_plans_updated_by_user_id",
                table: "cycle_count_plans",
                column: "updated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_planned_products_scheduled_for_cycle_count_plan~",
                table: "cycle_count_planned_products",
                columns: new[] { "scheduled_for", "cycle_count_plan_id" });

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_plan_events_campaign_id",
                table: "cycle_count_plan_events",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_plan_events_cycle_count_location_id",
                table: "cycle_count_plan_events",
                column: "cycle_count_location_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_plan_events_cycle_count_plan_id_recorded_at",
                table: "cycle_count_plan_events",
                columns: new[] { "cycle_count_plan_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_plan_events_responsible_user_id",
                table: "cycle_count_plan_events",
                column: "responsible_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_cycle_count_plans_users_created_by_user_id",
                table: "cycle_count_plans",
                column: "created_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_cycle_count_plans_users_updated_by_user_id",
                table: "cycle_count_plans",
                column: "updated_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_cycle_count_plans_users_created_by_user_id",
                table: "cycle_count_plans");

            migrationBuilder.DropForeignKey(
                name: "FK_cycle_count_plans_users_updated_by_user_id",
                table: "cycle_count_plans");

            migrationBuilder.DropTable(
                name: "cycle_count_plan_events");

            migrationBuilder.DropIndex(
                name: "IX_cycle_count_plans_created_by_user_id",
                table: "cycle_count_plans");

            migrationBuilder.DropIndex(
                name: "IX_cycle_count_plans_updated_by_user_id",
                table: "cycle_count_plans");

            migrationBuilder.DropIndex(
                name: "IX_cycle_count_planned_products_scheduled_for_cycle_count_plan~",
                table: "cycle_count_planned_products");

            migrationBuilder.DropColumn(
                name: "created_by_user_id",
                table: "cycle_count_plans");

            migrationBuilder.DropColumn(
                name: "updated_by_user_id",
                table: "cycle_count_plans");
        }
    }
}
