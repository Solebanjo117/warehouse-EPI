using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Inventory;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class PostgreSqlInventoryCollection : ICollectionFixture<PostgreSqlInventoryFixture>
{
    public const string CollectionName = "PostgreSQL inventory";
}

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class PostgreSqlInventoryTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Warehouse_map_saves_on_postgresql_without_an_expected_version()
    {
        var admin = await fixture.AddAdminAsync("Administrador del croquis", "3110");
        await using var db = fixture.CreateDbContext();
        db.Locations.Add(new Location { Code = "PG-MAP-AREA", Kind = LocationKind.Area });
        await db.SaveChangesAsync();
        var pins = new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey));
        var maps = new WarehouseMapService(db, pins, TimeProvider.System);
        var initialized = await maps.InitializeAsync(Guid.NewGuid(), admin.Id, admin.Pin, "Inicial");
        Assert.Equal(WarehouseMapSaveStatus.Success, initialized.Status);
        db.ChangeTracker.Clear();
        var map = await maps.GetAsync(true);
        var geometry = map.Elements.Concat(map.Unplaced)
            .Select(item => new WarehouseMapGeometry(item.Id, item.X, item.Y, item.Width, item.Height, item.Rotation, item.ZIndex, item.IsVisible))
            .ToArray();

        var layers = map.Layers.Select(item => new WarehouseMapLayerState(item.Code,
            item.Code == "ZONES" ? false : item.IsLocked)).ToArray();
        var architecture = map.Architecture.Select(item => new WarehouseMapArchitectureItem(item.Id,
            item.LayerCode, item.Kind, item.Label, item.X, item.Y, item.Width, item.Height, item.Rotation,
            item.CornerRadius, item.Points, item.StrokeToken, item.FillToken, item.StrokeWidth, item.IsDashed,
            item.ZIndex, item.IsLocked)).Append(new WarehouseMapArchitectureItem(Guid.NewGuid(), "ZONES",
                "Rectangle", "Zona PostgreSQL", 700, 400, 120, 80, 0, 0, [], "WARNING", "WARNING",
                2, false, 999, false)).ToArray();
        var result = await maps.SaveAsync(new(Guid.NewGuid(), admin.Id, admin.Pin, "Ajuste", geometry, layers, architecture, 12m, "IMPERIAL"));

        Assert.Equal(WarehouseMapSaveStatus.Success, result.Status);
        Assert.Equal(initialized.Version + 1, result.Version);
        Assert.Equal(6, await db.WarehouseMapLayers.CountAsync());
        Assert.Equal(18, await db.WarehouseMapArchitecturalElements.CountAsync());
        Assert.Equal(geometry.Length, await db.WarehouseMapElements.CountAsync());
        Assert.Equal(12m, (await db.WarehouseMapLayouts.AsNoTracking().SingleAsync()).ScaleUnitsPerInch);

        db.ChangeTracker.Clear();
        map = await maps.GetAsync(true);
        geometry = map.Elements.Concat(map.Unplaced).Select(item => new WarehouseMapGeometry(item.Id, item.X,
            item.Y, item.Width, item.Height, item.Rotation, item.ZIndex, item.IsVisible)).ToArray();
        architecture = map.Architecture.Concat(map.ArchivedArchitecture).Select(item => new WarehouseMapArchitectureItem(
            item.Id, item.LayerCode, item.Kind, item.Label, item.X, item.Y, item.Width, item.Height, item.Rotation,
            item.CornerRadius, item.Points, item.StrokeToken, item.FillToken, item.StrokeWidth, item.IsDashed,
            item.ZIndex, item.IsLocked, item.GroupId, item.IsArchived)).ToArray();
        var firstReference = ReferenceState('a');
        Assert.Equal(WarehouseMapSaveStatus.Success, (await maps.SaveAsync(new(Guid.NewGuid(), admin.Id, admin.Pin,
            "Fondo 1", geometry, map.Layers.Select(item => new WarehouseMapLayerState(item.Code, item.IsLocked)).ToArray(),
            architecture, 12m, "IMPERIAL", [firstReference]))).Status);
        var secondReference = ReferenceState('b');
        Assert.Equal(WarehouseMapSaveStatus.Success, (await maps.SaveAsync(new(Guid.NewGuid(), admin.Id, admin.Pin,
            "Reemplazar fondo", geometry, map.Layers.Select(item => new WarehouseMapLayerState(item.Code, item.IsLocked)).ToArray(),
            architecture, 12m, "IMPERIAL", [firstReference with { IsLocked = false, IsArchived = true }, secondReference]))).Status);
        Assert.Equal(2, await db.WarehouseMapReferenceImages.CountAsync());
        Assert.Single(await db.WarehouseMapReferenceImages.Where(item => !item.IsArchived).ToListAsync());
        Assert.Equal(geometry.Length, await db.WarehouseMapElements.CountAsync());
    }

    private static WarehouseMapReferenceImageState ReferenceState(char hashCharacter)
    {
        var hash = new string(hashCharacter, 64);
        return new(Guid.NewGuid(), $"plano-{hashCharacter}.png", $"{hash}.png", "image/png", hash, 800, 400,
            50, 50, 400, 200, 0, .35m, true, false, null, null, null, null, null);
    }

    [Fact]
    public async Task Phase_1194_migration_reverts_and_reapplies_without_touching_operational_rows()
    {
        await using var db = fixture.CreateDbContext();
        db.Locations.Add(new Location { Code = "PG-MIGRATION-MAP", Kind = LocationKind.Area });
        await db.SaveChangesAsync();
        var locationCount = await db.Locations.CountAsync();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20260824210000_ExpandWarehouseMapArchitectureStyles");
        Assert.Equal(locationCount, await db.Locations.CountAsync());

        await migrator.MigrateAsync("20260824233000_AddWarehouseMapProductivityScale");
        Assert.Equal(locationCount, await db.Locations.CountAsync());
        await migrator.MigrateAsync("20260825093000_AddWarehouseMapReferenceImages");
        Assert.Equal(locationCount, await db.Locations.CountAsync());
        var columns = await db.Database.SqlQueryRaw<string>("""
            SELECT column_name::text AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'warehouse_map_layouts'
              AND column_name IN ('measurement_system', 'scale_units_per_inch')
            ORDER BY column_name
            """).ToArrayAsync();
        Assert.Equal(["measurement_system", "scale_units_per_inch"], columns);
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.warehouse_map_reference_images') IS NOT NULL AS \"Value\"").SingleAsync());
    }

    [Fact]
    public async Task Rack_physical_presence_migration_and_save_preserve_operational_rows()
    {
        await using var db = fixture.CreateDbContext();
        db.Locations.AddRange(Enumerable.Range(1, 9).Select(number => new Location
        {
            Code = $"Y-8-{number}",
            Kind = LocationKind.Rack,
            RowCode = "Y",
            RackNumber = 8,
            PalletNumber = (short)number
        }));
        await db.SaveChangesAsync();
        var locationCount = await db.Locations.CountAsync();
        var movementCount = await db.InventoryMovements.CountAsync();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20260825093000_AddWarehouseMapReferenceImages");
        Assert.Equal(locationCount, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM locations").SingleAsync());
        Assert.Equal(movementCount, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM inventory_movements").SingleAsync());
        await migrator.MigrateAsync("20260825163000_AddLocationRackPhysicalPresence");
        Assert.Equal(locationCount, await db.Locations.CountAsync());

        var admin = await fixture.AddAdminAsync("Administrador de rack físico", "3198");
        var service = new LocationRackAdministrationService(db,
            new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey)), TimeProvider.System);
        var result = await service.SaveAsync(new(Guid.NewGuid(), admin.Id, "Y", 8,
            [1, 2, 3, 4, 5, 6], "Corrección PostgreSQL aislada", admin.Pin));

        Assert.Equal(LocationRackSaveStatus.Success, result.Status);
        Assert.Equal(9, await db.Locations.CountAsync(item => item.RowCode == "Y" && item.RackNumber == 8));
        Assert.Equal(3, await db.Locations.CountAsync(item => item.RowCode == "Y" && item.RackNumber == 8 && !item.IsPhysicallyPresent));
        Assert.Equal(movementCount, await db.InventoryMovements.CountAsync());
        Assert.Single(await db.LocationRackRevisions.Where(item => item.RowCode == "Y" && item.RackNumber == 8).ToListAsync());
    }

    [Fact]
    public async Task Queries_derive_zero_total_and_minimum_alert_without_a_balance_row()
    {
        var seed = await fixture.SeedAsync("PG-QUERY", "PG-AREA-0", "3100");
        await using var db = fixture.CreateDbContext();
        var product = await db.Products.SingleAsync(item => item.Id == seed.ProductId);
        product.MinimumStock = 3m;
        await db.SaveChangesAsync();
        var queries = new InventoryQueryService(db);

        Assert.Equal(0m, await queries.GetProductTotalAsync(seed.ProductId));
        Assert.Contains(await queries.GetBelowMinimumProductsAsync(), item =>
            item.ProductId == seed.ProductId && item.TotalQuantity == 0m);
    }

    [Fact]
    public async Task Balance_detail_filters_translate_on_postgresql()
    {
        var seed = await fixture.SeedAsync("PG-DETAIL-QUERY", "PG-DETAIL-AREA", "3106");
        var movement = await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Exit, seed.Pin,
            [new(seed.ProductId, 2.5m, SourceLocationId: seed.LocationId)]));
        Assert.Equal(InventoryMovementStatus.Success, movement.Status);

        await using var db = fixture.CreateDbContext();
        var queries = new InventoryQueryService(db);
        var byProduct = await queries.GetProductBalancesAsync(seed.ProductId);
        var byLocation = await queries.GetLocationContentsAsync(seed.LocationId);
        var negatives = await queries.GetNegativeBalancesAsync();

        Assert.Single(byProduct);
        Assert.Equal(seed.LocationId, byProduct[0].LocationId);
        Assert.Equal(-2.5m, byProduct[0].Quantity);
        Assert.Single(byLocation);
        Assert.Equal(seed.ProductId, byLocation[0].ProductId);
        Assert.Contains(negatives, item => item.ProductId == seed.ProductId && item.LocationId == seed.LocationId);
    }

    [Fact]
    public async Task Public_inventory_positions_translate_on_postgresql()
    {
        var seed = await fixture.SeedAsync("PG-PUBLIC-POSITION", "PG-PUBLIC-AREA", "3107");
        await using (var setup = fixture.CreateDbContext())
        {
            setup.ProductLocationAssignments.Add(new ProductLocationAssignment
            {
                ProductId = seed.ProductId,
                LocationId = seed.LocationId
            });
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var queries = new InventoryQueryService(db);
        var byProduct = await queries.GetProductInventoryAsync(seed.ProductId);
        var byLocation = await queries.GetLocationInventoryAsync(seed.LocationId);

        Assert.Single(byProduct);
        Assert.True(byProduct[0].HasActiveAssignment);
        Assert.Equal(0m, byProduct[0].Quantity);
        Assert.Single(byLocation);
        Assert.Equal(seed.ProductId, byLocation[0].ProductId);
    }

    [Fact]
    public async Task Negative_alerts_use_the_net_position_across_lots_on_postgresql()
    {
        var seed = await fixture.SeedAsync("PG-ALERT-NET", "PG-ALERT-AREA", "3117");
        await using var db = fixture.CreateDbContext();
        var oldLot = new ProductLot
        {
            ProductId = seed.ProductId,
            Number = "AUTO-20260819",
            NormalizedNumber = "AUTO-20260819"
        };
        var currentLot = new ProductLot
        {
            ProductId = seed.ProductId,
            Number = "AUTO-20260820",
            NormalizedNumber = "AUTO-20260820"
        };
        db.AddRange(oldLot, currentLot);
        db.InventoryBalances.AddRange(
            new InventoryBalance
            {
                ProductId = seed.ProductId,
                LocationId = seed.LocationId,
                LotId = oldLot.Id,
                Quantity = -2m
            },
            new InventoryBalance
            {
                ProductId = seed.ProductId,
                LocationId = seed.LocationId,
                LotId = currentLot.Id,
                Quantity = 2m
            });
        await db.SaveChangesAsync();
        var queries = new InventoryQueryService(db);

        Assert.Equal(0m, await queries.GetProductTotalAsync(seed.ProductId));
        Assert.Empty((await queries.GetNegativeAlertPageAsync("PG-ALERT-NET", 1, 25)).Items);
    }

    [Fact]
    public async Task Concurrent_entries_do_not_lose_updates()
    {
        var seed = await fixture.SeedAsync("PG-CONCURRENT", "PG-AREA-1", "3101");
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(index =>
            fixture.ConfirmAsync(new(
                Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
                [new(seed.ProductId, 1m, DestinationLocationId: seed.LocationId)]))));

        Assert.All(results, result => Assert.Equal(InventoryMovementStatus.Success, result.Status));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(5m, (await db.InventoryBalances.SingleAsync(balance =>
            balance.ProductId == seed.ProductId && balance.LocationId == seed.LocationId)).Quantity);
        Assert.Equal(5, await db.InventoryMovements.CountAsync(movement =>
            movement.Lines.Any(line => line.ProductId == seed.ProductId)));
    }

    [Fact]
    public async Task Concurrent_retries_with_same_uuid_create_one_movement()
    {
        var seed = await fixture.SeedAsync("PG-IDEMPOTENT", "PG-AREA-2", "3102");
        var command = new InventoryMovementCommand(
            Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
            [new(seed.ProductId, 2m, DestinationLocationId: seed.LocationId)]);

        var results = await Task.WhenAll(fixture.ConfirmAsync(command), fixture.ConfirmAsync(command));

        Assert.All(results, result => Assert.Equal(InventoryMovementStatus.Success, result.Status));
        Assert.Equal(results[0].MovementId, results[1].MovementId);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.InventoryMovements.CountAsync(movement => movement.OperationId == command.OperationId));
        Assert.Equal(2m, (await db.InventoryBalances.SingleAsync(balance =>
            balance.ProductId == seed.ProductId && balance.LocationId == seed.LocationId)).Quantity);
    }

    [Fact]
    public async Task Adjustment_rejects_a_real_postgresql_xmin_change()
    {
        var seed = await fixture.SeedAsync("PG-XMIN", "PG-AREA-3", "3103");
        await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
            [new(seed.ProductId, 10m, DestinationLocationId: seed.LocationId)]));
        uint originalVersion;
        await using (var db = fixture.CreateDbContext())
        {
            originalVersion = (await db.InventoryBalances.AsNoTracking().SingleAsync(balance =>
                balance.ProductId == seed.ProductId && balance.LocationId == seed.LocationId)).Version;
        }
        await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
            [new(seed.ProductId, 1m, DestinationLocationId: seed.LocationId)]));

        var result = await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Adjustment, seed.Pin,
            [new(seed.ProductId, 4m, LocationId: seed.LocationId, ExpectedBalanceVersion: originalVersion)]));

        Assert.Equal(InventoryMovementStatus.BalanceChanged, result.Status);
        await using var verification = fixture.CreateDbContext();
        Assert.Equal(11m, (await verification.InventoryBalances.SingleAsync(balance =>
            balance.ProductId == seed.ProductId && balance.LocationId == seed.LocationId)).Quantity);
    }

    [Fact]
    public async Task Initial_adjustment_accepts_missing_balance_version_zero()
    {
        var seed = await fixture.SeedAsync("PG-INITIAL-COUNT", "PG-AREA-4", "3104");

        var result = await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Adjustment, seed.Pin,
            [new(seed.ProductId, 14.5m, LocationId: seed.LocationId, ExpectedBalanceVersion: 0)],
            Notes: "Conteo inicial"));

        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(14.5m, (await db.InventoryBalances.SingleAsync(balance =>
            balance.ProductId == seed.ProductId && balance.LocationId == seed.LocationId)).Quantity);
    }

    [Fact]
    public async Task Missing_balance_version_zero_rejects_when_another_movement_created_the_balance()
    {
        var seed = await fixture.SeedAsync("PG-INITIAL-RACE", "PG-AREA-5", "3105");
        await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
            [new(seed.ProductId, 1m, DestinationLocationId: seed.LocationId)]));

        var result = await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Adjustment, seed.Pin,
            [new(seed.ProductId, 8m, LocationId: seed.LocationId, ExpectedBalanceVersion: 0)],
            Notes: "Conteo que partió de saldo inexistente"));

        Assert.Equal(InventoryMovementStatus.BalanceChanged, result.Status);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(1m, (await db.InventoryBalances.SingleAsync(balance =>
            balance.ProductId == seed.ProductId && balance.LocationId == seed.LocationId)).Quantity);
    }

    [Fact]
    public async Task Failed_replacement_rolls_back_reversal_and_correction_on_postgresql()
    {
        var seed = await fixture.SeedAsync("PG-CORRECTION-ROLLBACK", "PG-AREA-CORRECTION", "3108");
        var original = await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
            [new(seed.ProductId, 3m, DestinationLocationId: seed.LocationId)]));
        var admin = await fixture.AddAdminAsync("Administrador de prueba", "3109");
        await using var db = fixture.CreateDbContext();
        var pins = new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey));
        var movements = new InventoryMovementService(db, pins, TimeProvider.System);
        var corrections = new InventoryCorrectionService(db, pins, movements, TimeProvider.System);
        var movementsBeforeReplacement = await db.InventoryMovements.CountAsync(item => item.Lines
            .Any(line => line.ProductId == seed.ProductId));

        var result = await corrections.ConfirmAsync(new(
            Guid.NewGuid(),
            original.MovementId!.Value,
            admin.Id,
            admin.Pin,
            "Reemplazo inválido",
            new(InventoryMovementType.Entry, [new(seed.ProductId, 1m)])));

        Assert.Equal(InventoryCorrectionStatus.ValidationFailed, result.Status);
        db.ChangeTracker.Clear();
        Assert.Equal(movementsBeforeReplacement, await db.InventoryMovements.CountAsync(item => item.Lines
            .Any(line => line.ProductId == seed.ProductId)));
        Assert.Equal(1, await db.InventoryMovements.CountAsync(item => item.Id == original.MovementId));
        Assert.False(await db.InventoryMovementCorrections.AnyAsync(item => item.OriginalMovementId == original.MovementId));
        Assert.Equal(3m, await db.InventoryBalances.Where(item =>
            item.ProductId == seed.ProductId && item.LocationId == seed.LocationId).SumAsync(item => item.Quantity));
    }

    [Fact]
    public async Task Effective_movements_query_translates_and_excludes_corrections_on_postgresql()
    {
        var seed = await fixture.SeedAsync("PG-REPORT-SKU", "PG-REPORT-LOC", "3115");
        var admin = await fixture.AddAdminAsync("Admin Reporte", "3116");

        // 1. Movimiento normal
        var normal = await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
            [new(seed.ProductId, 10m, DestinationLocationId: seed.LocationId)]));

        // 2. Movimiento que será corregido
        var original = await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
            [new(seed.ProductId, 5m, DestinationLocationId: seed.LocationId)]));

        // 3. Aplicar corrección con reemplazo
        await using (var db = fixture.CreateDbContext())
        {
            var pins = new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey));
            var movements = new InventoryMovementService(db, pins, TimeProvider.System);
            var corrections = new InventoryCorrectionService(db, pins, movements, TimeProvider.System);

            var corrResult = await corrections.ConfirmAsync(new(
                Guid.NewGuid(),
                original.MovementId!.Value,
                admin.Id,
                admin.Pin,
                "Corrección efectiva en PostgreSQL",
                new(InventoryMovementType.Entry, [new(seed.ProductId, 8m, DestinationLocationId: seed.LocationId)])));

            Assert.Equal(InventoryCorrectionStatus.Success, corrResult.Status);
        }

        // 4. Consultar movimientos efectivos usando EffectiveMovementQuery
        await using (var db = fixture.CreateDbContext())
        {
            var filter = new MovementReportFilter(
                Sku: "report-sk",
                LocationCode: "report-lo");
            var effectiveMovements = await db.InventoryMovements
                .AsNoTracking()
                .ApplyFilter(db, filter)
                .Include(m => m.ResponsibleUser)
                .Include(m => m.Lines).ThenInclude(l => l.Product)
                .Include(m => m.Lines).ThenInclude(l => l.Unit)
                .Include(m => m.Lines).ThenInclude(l => l.DestinationLocation)
                .Include(m => m.Lines).ThenInclude(l => l.BalanceChanges).ThenInclude(c => c.Location)
                .OrderByDescending(m => m.OccurredAt)
                .ToListAsync();

            // Debe contener el normal (10) y el reemplazo (8), pero NO el original (5) ni el reverso
            Assert.Equal(2, effectiveMovements.Count);
            Assert.Contains(effectiveMovements, m => m.Id == normal.MovementId);
            Assert.DoesNotContain(effectiveMovements, m => m.Id == original.MovementId);

            var rowDtos = effectiveMovements.Select(EffectiveMovementQuery.ProjectToRowDto).ToArray();
            Assert.Equal(2, rowDtos.Length);
            Assert.All(rowDtos, dto => Assert.Equal(1, dto.DistinctSkuCount));
            Assert.All(rowDtos, dto => Assert.NotEmpty(dto.Lines[0].BalanceChanges));

            var byCompleteValues = await db.InventoryMovements.AsNoTracking()
                .ApplyFilter(db, new MovementReportFilter(
                    Sku: "PG-REPORT-SKU",
                    LocationCode: "PG-REPORT-LOC"))
                .Select(movement => movement.Id)
                .ToListAsync();
            Assert.Equal(2, byCompleteValues.Count);

            var folio = normal.MovementId!.Value.ToString("N")[..8];
            var byFolio = await db.InventoryMovements.AsNoTracking()
                .ApplyFilter(db, new MovementReportFilter(Search: folio))
                .Select(movement => movement.Id)
                .ToListAsync();
            Assert.Contains(normal.MovementId.Value, byFolio);
        }
    }

    [Fact]
    public async Task Wip_report_search_page_and_export_translate_on_postgresql()
    {
        var seed = await fixture.SeedAsync("PG-WIP-REPORT", "PG-WIP-SOURCE", "3118");
        Guid wipAreaId;
        await using (var setup = fixture.CreateDbContext())
        {
            var source = await setup.Locations.SingleAsync(item => item.Id == seed.LocationId);
            source.Kind = LocationKind.Rack;
            var wipArea = new Location
            {
                Code = "PG-WIP-AREA",
                Kind = LocationKind.Area,
                OperationalRole = LocationOperationalRole.Wip
            };
            setup.Locations.Add(wipArea);
            await setup.SaveChangesAsync();
            wipAreaId = wipArea.Id;
        }

        var issue = await fixture.ConfirmAsync(new(
            Guid.NewGuid(),
            InventoryMovementType.Transfer,
            seed.Pin,
            [new(seed.ProductId, 4m, seed.LocationId, wipAreaId)],
            Purpose: InventoryMovementPurpose.ProductionIssue,
            OperationalAreaId: wipAreaId));
        Assert.Equal(InventoryMovementStatus.Success, issue.Status);

        await using var db = fixture.CreateDbContext();
        var supplier = await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Exit, seed.Pin,
            [new(seed.ProductId, 1m, SourceLocationId: wipAreaId)],
            Reference: "PG-RMA", Purpose: InventoryMovementPurpose.WipSupplierReturn,
            OperationalAreaId: wipAreaId));
        Assert.Equal(InventoryMovementStatus.Success, supplier.Status);

        var reports = new WipReportService(db, new WarehouseClock(new WarehouseSettingsService(db)));
        var page = await reports.GetTrackedPageAsync(new(null, null, "PG-WIP-REPORT", wipAreaId), 1, 25);

        Assert.Contains(page.Inventory, row => row.ProductSku == "PG-WIP-REPORT" && row.Quantity == 3m);
        Assert.Contains(page.Activity, row => row.MovementId == issue.MovementId && row.Delta == 4m && row.Category == "Recibido");
        Assert.Contains(page.Activity, row => row.MovementId == supplier.MovementId && row.Delta == -1m && row.Category == "Devolución a proveedor");
    }
}

public sealed class PostgreSqlInventoryFixture : IAsyncLifetime
{
    private const string TestDatabase = "warehouse_epi_test";
    internal const string LookupKey =
        "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private string connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var configuredTest = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION");
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        var source = configuredTest ?? configuration.GetConnectionString("Warehouse") ??
            throw new InvalidOperationException(
                "Configure WAREHOUSE_EPI_TEST_CONNECTION o ConnectionStrings:Warehouse en User Secrets.");
        var sourceBuilder = new NpgsqlConnectionStringBuilder(source);
        if (configuredTest is not null && !string.Equals(sourceBuilder.Database, TestDatabase, StringComparison.Ordinal))
            throw new InvalidOperationException($"La conexión de pruebas debe apuntar exclusivamente a {TestDatabase}.");

        var adminBuilder = new NpgsqlConnectionStringBuilder(sourceBuilder.ConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var reset = admin.CreateCommand();
            reset.CommandText = $"DROP DATABASE IF EXISTS {TestDatabase} WITH (FORCE); CREATE DATABASE {TestDatabase};";
            await reset.ExecuteNonQueryAsync();
        }

        sourceBuilder.Database = TestDatabase;
        connectionString = sourceBuilder.ConnectionString;
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        return Task.CompletedTask;
    }

    public WarehouseDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    public async Task<InventoryMovementResult> ConfirmAsync(InventoryMovementCommand command)
    {
        await using var db = CreateDbContext();
        var pinService = new UserPinService(db, new PinProtector(LookupKey));
        return await new InventoryMovementService(db, pinService, TimeProvider.System).ConfirmAsync(command);
    }

    public async Task<InventorySeed> SeedAsync(string sku, string locationCode, string pin)
    {
        await using var db = CreateDbContext();
        var pinService = new UserPinService(db, new PinProtector(LookupKey));
        var user = new User
        {
            FullName = $"Operador {sku}",
            RoleId = 2,
            PinLookup = string.Empty,
            PinHash = string.Empty
        };
        Assert.Equal(PinAssignmentResult.Success, await pinService.AssignAsync(user, pin));
        var product = new Product { Sku = sku, BaseUnitId = 1 };
        var location = new Location { Code = locationCode, Kind = LocationKind.Area };
        db.AddRange(user, product, location);
        await db.SaveChangesAsync();
        return new(product.Id, location.Id, pin);
    }

    public async Task<InventoryAdmin> AddAdminAsync(string name, string pin)
    {
        await using var db = CreateDbContext();
        var pinService = new UserPinService(db, new PinProtector(LookupKey));
        var user = new User
        {
            FullName = name,
            RoleId = 1,
            PinLookup = string.Empty,
            PinHash = string.Empty
        };
        Assert.Equal(PinAssignmentResult.Success, await pinService.AssignAsync(user, pin));
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new(user.Id, pin);
    }
}

public sealed record InventorySeed(Guid ProductId, Guid LocationId, string Pin);
public sealed record InventoryAdmin(Guid Id, string Pin);
