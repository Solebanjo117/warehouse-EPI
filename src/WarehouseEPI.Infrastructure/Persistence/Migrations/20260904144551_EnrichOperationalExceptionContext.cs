using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnrichOperationalExceptionContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reason_text",
                table: "operational_exception_cases",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE operational_exception_cases
                SET reason_text = CASE category
                    WHEN 'NEGATIVE_INVENTORY' THEN 'El saldo registrado es menor que cero y requiere revisión.'
                    WHEN 'BELOW_MINIMUM' THEN 'La existencia está por debajo del mínimo configurado.'
                    WHEN 'UNASSIGNED_BALANCE' THEN 'Existe inventario sin una asignación activa producto-ubicación.'
                    WHEN 'RESTRICTED_INVENTORY' THEN 'Existe inventario en una ubicación bloqueada o inactiva.'
                    WHEN 'STAGNANT_INVENTORY' THEN 'El producto conserva existencia sin una salida efectiva reciente.'
                    WHEN 'CYCLE_COUNT_STALE' THEN 'El saldo cambió durante el conteo y requiere reconteo.'
                    WHEN 'CYCLE_COUNT_PENDING' THEN 'El conteo está pendiente de revisión o reconteo.'
                    WHEN 'AGED_WIP' THEN 'La existencia permaneció en WIP más tiempo que el límite configurado.'
                    ELSE 'La condición operativa requiere revisión.'
                END
                WHERE reason_text IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "reason_text",
                table: "operational_exception_cases",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reason_text",
                table: "operational_exception_cases");
        }
    }
}
