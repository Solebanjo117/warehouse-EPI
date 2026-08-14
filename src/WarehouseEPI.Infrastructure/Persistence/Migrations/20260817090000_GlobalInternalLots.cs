using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GlobalInternalLots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                WITH local_day AS (
                    SELECT timezone('America/Matamoros', now())::date AS lot_date,
                           to_char(timezone('America/Matamoros', now()), 'YYYYMMDD') AS day_code
                ), legacy_products AS (
                    SELECT product_id FROM inventory_balances WHERE lot_id IS NULL
                    UNION
                    SELECT line.product_id
                    FROM inventory_movement_lines AS line
                    JOIN inventory_balance_changes AS change ON change.movement_line_id = line.id
                    WHERE change.lot_id IS NULL
                )
                INSERT INTO product_lots (id, product_id, number, normalized_number, lot_date, created_at)
                SELECT md5('global-internal-lot:' || product_id::text || ':' || day_code)::uuid,
                       product_id, 'AUTO-' || day_code, 'AUTO-' || day_code, lot_date, now()
                FROM legacy_products CROSS JOIN local_day
                ON CONFLICT (product_id, normalized_number) DO NOTHING;

                WITH local_day AS (
                    SELECT to_char(timezone('America/Matamoros', now()), 'YYYYMMDD') AS day_code
                )
                UPDATE inventory_balances AS balance
                SET lot_id = lot.id
                FROM product_lots AS lot CROSS JOIN local_day
                WHERE balance.lot_id IS NULL
                  AND lot.product_id = balance.product_id
                  AND lot.normalized_number = 'AUTO-' || day_code;
                """);
            migrationBuilder.DropColumn(
                name: "tracks_lots",
                table: "products");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "tracks_lots",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
