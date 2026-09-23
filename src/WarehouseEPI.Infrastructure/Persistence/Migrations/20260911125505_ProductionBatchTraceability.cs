using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WarehouseDbContext))]
[Migration("20260911125505_ProductionBatchTraceability")]
public partial class ProductionBatchTraceability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        const string resourceName = "WarehouseEPI.Infrastructure.Migrations.20260911125505_ProductionBatchTraceability.sql";
        using var stream = typeof(ProductionBatchTraceability).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"No se encontró el recurso {resourceName}.");
        using var reader = new StreamReader(stream);
        var script = reader.ReadToEnd();
        var bodyStart = script.IndexOf("START TRANSACTION;", StringComparison.Ordinal);
        var historyStart = script.IndexOf("INSERT INTO \"__EFMigrationsHistory\"", StringComparison.Ordinal);
        if (bodyStart < 0 || historyStart < 0 || historyStart <= bodyStart)
            throw new InvalidOperationException("El SQL revisable de trazabilidad no tiene el formato esperado.");
        var body = script[(bodyStart + "START TRANSACTION;".Length)..historyStart].Trim();
        migrationBuilder.Sql(body);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE production_events DROP CONSTRAINT IF EXISTS "FK_production_events_production_batches_batch_id";
            ALTER TABLE production_events DROP CONSTRAINT IF EXISTS "FK_production_events_production_events_related_event_id";
            DROP TABLE IF EXISTS production_batch_material_consumptions;
            DROP TABLE IF EXISTS production_order_material_plans;
            DROP TABLE IF EXISTS production_recipe_lines;
            DROP TABLE IF EXISTS production_batch_results;
            DROP TABLE IF EXISTS production_recipes;
            DROP TABLE IF EXISTS production_batches;
            DROP INDEX IF EXISTS "IX_production_events_batch_id";
            DROP INDEX IF EXISTS "IX_production_events_related_event_id";
            ALTER TABLE production_work_orders DROP COLUMN IF EXISTS recipe_version;
            ALTER TABLE production_work_orders DROP COLUMN IF EXISTS uses_batch_traceability;
            ALTER TABLE production_events DROP COLUMN IF EXISTS batch_id;
            ALTER TABLE production_events DROP COLUMN IF EXISTS related_event_id;
            """);
    }
}
