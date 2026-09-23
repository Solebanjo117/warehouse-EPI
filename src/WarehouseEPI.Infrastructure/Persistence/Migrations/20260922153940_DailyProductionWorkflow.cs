using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DailyProductionWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "production_capture_submissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    responsible_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_capture_submissions", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_capture_submissions_users_responsible_user_id",
                        column: x => x.responsible_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_carryover_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    week_id = table.Column<Guid>(type: "uuid", nullable: false),
                    planned_date = table.Column<DateOnly>(type: "date", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    area = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_carryover_plans", x => x.id);
                    table.CheckConstraint("ck_carryover_plan_quantity", "quantity >= 0");
                    table.ForeignKey(
                        name: "FK_production_carryover_plans_production_schedule_weeks_week_id",
                        column: x => x.week_id,
                        principalTable: "production_schedule_weeks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_carryover_plans_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_carryover_plans_users_updated_by_user_id",
                        column: x => x.updated_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_capture_submission_items",
                columns: table => new
                {
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capture_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_capture_submission_items", x => new { x.submission_id, x.capture_id });
                    table.ForeignKey(
                        name: "FK_production_capture_submission_items_production_capture_subm~",
                        column: x => x.submission_id,
                        principalTable: "production_capture_submissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_capture_submission_items_production_daily_captur~",
                        column: x => x.capture_id,
                        principalTable: "production_daily_captures",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_capture_submission_items_capture_id",
                table: "production_capture_submission_items",
                column: "capture_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_capture_submissions_operation_id",
                table: "production_capture_submissions",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_capture_submissions_responsible_user_id",
                table: "production_capture_submissions",
                column: "responsible_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_carryover_plans_product_id",
                table: "production_carryover_plans",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_carryover_plans_updated_by_user_id",
                table: "production_carryover_plans",
                column: "updated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_carryover_plans_week_id_planned_date_product_id_~",
                table: "production_carryover_plans",
                columns: new[] { "week_id", "planned_date", "product_id", "area" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_capture_submission_items");

            migrationBuilder.DropTable(
                name: "production_carryover_plans");

            migrationBuilder.DropTable(
                name: "production_capture_submissions");
        }
    }
}
