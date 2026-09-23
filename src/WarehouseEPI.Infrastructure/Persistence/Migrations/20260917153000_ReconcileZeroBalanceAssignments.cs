using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WarehouseDbContext))]
[Migration("20260917153000_ReconcileZeroBalanceAssignments")]
public sealed class ReconcileZeroBalanceAssignments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            WITH zero_assignments AS (
                SELECT assignment.product_id, assignment.location_id
                FROM product_location_assignments AS assignment
                LEFT JOIN inventory_balances AS balance
                    ON balance.product_id = assignment.product_id
                    AND balance.location_id = assignment.location_id
                WHERE assignment.is_active = TRUE
                GROUP BY assignment.product_id, assignment.location_id
                HAVING COALESCE(SUM(balance.quantity), 0) = 0
            )
            UPDATE products AS product
            SET default_entry_location_id = NULL
            FROM zero_assignments AS exhausted
            WHERE product.id = exhausted.product_id
              AND product.default_entry_location_id = exhausted.location_id;

            WITH zero_assignments AS (
                SELECT assignment.product_id, assignment.location_id
                FROM product_location_assignments AS assignment
                LEFT JOIN inventory_balances AS balance
                    ON balance.product_id = assignment.product_id
                    AND balance.location_id = assignment.location_id
                WHERE assignment.is_active = TRUE
                GROUP BY assignment.product_id, assignment.location_id
                HAVING COALESCE(SUM(balance.quantity), 0) = 0
            )
            UPDATE product_location_assignments AS assignment
            SET is_active = FALSE,
                updated_at = CURRENT_TIMESTAMP
            FROM zero_assignments AS exhausted
            WHERE assignment.product_id = exhausted.product_id
              AND assignment.location_id = exhausted.location_id;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // La limpieza no puede reconstruir con seguridad cuáles asignaciones en cero
        // eran intencionales ni cuál era la ubicación principal histórica.
    }
}
