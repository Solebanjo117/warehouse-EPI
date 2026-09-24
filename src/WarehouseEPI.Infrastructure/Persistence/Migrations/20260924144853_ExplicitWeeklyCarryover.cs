using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExplicitWeeklyCarryover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "explicit_carryover",
                table: "production_schedule_weeks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "production_week_openings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    week_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_week_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    area = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    source_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_week_openings", x => x.id);
                    table.CheckConstraint("ck_week_opening_quantity", "quantity >= 0");
                    table.ForeignKey(
                        name: "FK_production_week_openings_production_schedule_lines_source_l~",
                        column: x => x.source_line_id,
                        principalTable: "production_schedule_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_week_openings_production_schedule_weeks_source_w~",
                        column: x => x.source_week_id,
                        principalTable: "production_schedule_weeks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_week_openings_production_schedule_weeks_week_id",
                        column: x => x.week_id,
                        principalTable: "production_schedule_weeks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_week_openings_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_week_openings_product_id",
                table: "production_week_openings",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_week_openings_source_line_id",
                table: "production_week_openings",
                column: "source_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_week_openings_source_week_id",
                table: "production_week_openings",
                column: "source_week_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_week_openings_week_id_source_week_id_source_line~",
                table: "production_week_openings",
                columns: new[] { "week_id", "source_week_id", "source_line_id", "area" },
                unique: true);
            // Preserve every historical/imported or already used calculation. Old intentions stay
            // in their table; they are never converted into admitted opening quantities.
            migrationBuilder.Sql("""
                UPDATE production_schedule_weeks w SET explicit_carryover = TRUE, version = version + 1
                WHERE w.status <> 'Closed' AND w.origin <> 'ExcelImport'
                  AND NOT EXISTS (SELECT 1 FROM production_daily_captures c WHERE c.week_id = w.id)
                  AND NOT EXISTS (SELECT 1 FROM production_schedule_lines l
                    JOIN production_daily_capture_allocations a ON a.schedule_line_id = l.id WHERE l.week_id = w.id)
                  AND NOT EXISTS (SELECT 1 FROM production_schedule_lines l
                    JOIN production_batches b ON b.work_order_id = l.work_order_id
                    JOIN production_batch_results r ON r.batch_id = b.id WHERE l.week_id = w.id)
                  AND NOT EXISTS (SELECT 1 FROM production_schedule_lines l
                    JOIN production_events e ON e.work_order_id = l.work_order_id
                    WHERE l.week_id = w.id AND e.type IN ('Processed', 'Reworked', 'Delivered', 'Received', 'WarehouseReceived'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_week_openings");

            migrationBuilder.DropColumn(
                name: "explicit_carryover",
                table: "production_schedule_weeks");
        }
    }
}
