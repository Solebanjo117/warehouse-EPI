using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CycleCountScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cycle_count_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    anchor_date = table.Column<DateOnly>(type: "date", nullable: false),
                    next_due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cycle_count_plans", x => x.id);
                    table.ForeignKey(
                        name: "FK_cycle_count_plans_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_plans_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cycle_count_planned_products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_count_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_count_plan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scheduled_for = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cycle_count_planned_products", x => x.id);
                    table.ForeignKey(
                        name: "FK_cycle_count_planned_products_cycle_count_locations_cycle_co~",
                        column: x => x.cycle_count_location_id,
                        principalTable: "cycle_count_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_planned_products_cycle_count_plans_cycle_count_~",
                        column: x => x.cycle_count_plan_id,
                        principalTable: "cycle_count_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_planned_products_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_planned_products_cycle_count_location_id_produc~",
                table: "cycle_count_planned_products",
                columns: new[] { "cycle_count_location_id", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_planned_products_cycle_count_plan_id",
                table: "cycle_count_planned_products",
                column: "cycle_count_plan_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_planned_products_product_id",
                table: "cycle_count_planned_products",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_plans_is_active_next_due_date",
                table: "cycle_count_plans",
                columns: new[] { "is_active", "next_due_date" });

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_plans_location_id",
                table: "cycle_count_plans",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_plans_product_id_location_id",
                table: "cycle_count_plans",
                columns: new[] { "product_id", "location_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cycle_count_planned_products");

            migrationBuilder.DropTable(
                name: "cycle_count_plans");
        }
    }
}
