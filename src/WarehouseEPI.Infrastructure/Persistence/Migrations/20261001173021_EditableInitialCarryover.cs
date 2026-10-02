using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EditableInitialCarryover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "production_initial_balances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    week_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    area = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_initial_balances", x => x.id);
                    table.CheckConstraint("ck_initial_balance_quantity", "quantity >= 0");
                    table.ForeignKey(
                        name: "FK_production_initial_balances_production_schedule_weeks_week_~",
                        column: x => x.week_id,
                        principalTable: "production_schedule_weeks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_initial_balances_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_initial_balances_product_id",
                table: "production_initial_balances",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_initial_balances_week_id_product_id_area",
                table: "production_initial_balances",
                columns: new[] { "week_id", "product_id", "area" },
                unique: true);

            // Keep physical provenance untouched; consolidate only the reporting total.
            // Closed history retains its original calculation until explicitly reopened/edited.
            migrationBuilder.Sql("""
                INSERT INTO production_initial_balances (id, week_id, product_id, area, quantity, version)
                SELECT md5(o.week_id::text || '/' || o.product_id::text || '/' || o.area)::uuid,
                       o.week_id, o.product_id, o.area, SUM(o.quantity), 1
                FROM production_week_openings o
                JOIN production_schedule_weeks w ON w.id = o.week_id
                WHERE w.explicit_carryover AND w.status <> 'Closed'
                GROUP BY o.week_id, o.product_id, o.area;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_initial_balances");
        }
    }
}
