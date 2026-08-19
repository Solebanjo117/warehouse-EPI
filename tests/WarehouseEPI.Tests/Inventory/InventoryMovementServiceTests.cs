using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Inventory;

public sealed class InventoryMovementServiceTests
{
    [Fact]
    public async Task Multi_line_entry_records_each_product_once()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.AddProductAsync("MULTI-ONE");
        var second = await fixture.AddProductAsync("MULTI-TWO");
        var firstLocation = await fixture.AddLocationAsync("MULTI-A");
        var secondLocation = await fixture.AddLocationAsync("MULTI-B");

        var result = await fixture.Service.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, fixture.OperatorPin,
            [
                new(first.Id, 1.25m, DestinationLocationId: firstLocation.Id),
                new(second.Id, 2.5m, DestinationLocationId: secondLocation.Id)
            ]));

        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        Assert.Equal(2, result.ResultingBalances.Count);
        Assert.Equal(2, await fixture.Db.InventoryMovementLines.CountAsync());
        Assert.Equal(2, await fixture.Db.InventoryBalanceChanges.CountAsync());
    }

    [Fact]
    public async Task Entry_creates_balance_assignment_and_auditable_change()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("ENTRY-ONE");
        var location = await fixture.AddLocationAsync("RECEIVING");

        var result = await fixture.Service.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, fixture.OperatorPin,
            [new(product.Id, 12.5m, DestinationLocationId: location.Id)]));

        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        Assert.Equal(12.5m, Assert.Single(result.ResultingBalances).Quantity);
        Assert.False(result.HasNegativeBalance);
        Assert.True((await fixture.Db.ProductLocationAssignments.SingleAsync()).IsActive);
        var movement = await fixture.Db.InventoryMovements
            .Include(item => item.Lines).ThenInclude(line => line.BalanceChanges).SingleAsync();
        var change = Assert.Single(Assert.Single(movement.Lines).BalanceChanges);
        Assert.Equal(0m, change.PreviousQuantity);
        Assert.Equal(12.5m, change.DeltaQuantity);
        Assert.Equal(12.5m, change.ResultingQuantity);
    }

    [Fact]
    public async Task Entry_that_overflows_an_existing_daily_lot_propagates_the_historical_exception()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("MAXIMUM-BALANCE");
        var location = await fixture.AddLocationAsync("MAXIMUM-AREA");
        var maximum = 99_999_999_999_999.9999m;
        var first = await fixture.Service.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, fixture.OperatorPin,
            [new(product.Id, maximum, DestinationLocationId: location.Id)]));
        var movementsBeforeOverflow = await fixture.Db.InventoryMovements.CountAsync(item => item.Lines
            .Any(line => line.ProductId == product.Id));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, fixture.OperatorPin,
            [new(product.Id, 0.0001m, DestinationLocationId: location.Id)])));

        Assert.Equal("El saldo resultante excede la precisión numeric(18,4).", exception.Message);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(movementsBeforeOverflow, await fixture.Db.InventoryMovements.CountAsync(item => item.Lines
            .Any(line => line.ProductId == product.Id)));
        Assert.Equal(1, await fixture.Db.InventoryMovements.CountAsync(item => item.Id == first.MovementId));
    }

    [Fact]
    public async Task Exit_allows_negative_balance_and_returns_warning()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("NEGATIVE");
        var location = await fixture.AddLocationAsync("SHIPPING");

        var result = await fixture.Service.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Exit, fixture.OperatorPin,
            [new(product.Id, 3m, SourceLocationId: location.Id)]));

        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        Assert.True(result.HasNegativeBalance);
        Assert.Equal(-3m, Assert.Single(result.ResultingBalances).Quantity);
    }

    [Fact]
    public async Task Transfer_updates_both_locations_and_preserves_total()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("TRANSFER");
        var source = await fixture.AddLocationAsync("A-1-1");
        var destination = await fixture.AddLocationAsync("A-1-2");
        await fixture.Service.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, fixture.OperatorPin,
            [new(product.Id, 10m, DestinationLocationId: source.Id)]));

        var result = await fixture.Service.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Transfer, fixture.OperatorPin,
            [new(product.Id, 4m, SourceLocationId: source.Id, DestinationLocationId: destination.Id)]));

        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        Assert.Contains(result.ResultingBalances, balance => balance.LocationId == source.Id && balance.Quantity == 6m);
        Assert.Contains(result.ResultingBalances, balance => balance.LocationId == destination.Id && balance.Quantity == 4m);
        Assert.Equal(10m, await new InventoryQueryService(fixture.Db).GetProductTotalAsync(product.Id));
    }

    [Fact]
    public async Task Adjustment_uses_final_count_and_rejects_stale_version()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("COUNTED");
        var location = await fixture.AddLocationAsync("COUNTING");
        await fixture.Service.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, fixture.OperatorPin,
            [new(product.Id, 7m, DestinationLocationId: location.Id)]));
        var current = await fixture.Db.InventoryBalances.AsNoTracking().SingleAsync();

        var stale = await fixture.Service.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Adjustment, fixture.OperatorPin,
            [new(product.Id, 2m, LocationId: location.Id, ExpectedBalanceVersion: current.Version + 1)]));
        Assert.Equal(InventoryMovementStatus.BalanceChanged, stale.Status);

        var result = await fixture.Service.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Adjustment, fixture.OperatorPin,
            [new(product.Id, -2m, LocationId: location.Id, ExpectedBalanceVersion: current.Version)]));

        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        Assert.True(result.HasNegativeBalance);
        var line = await fixture.Db.InventoryMovementLines.OrderBy(item => item.LineNumber).LastAsync();
        Assert.Equal(7m, line.PreviousQuantity);
        Assert.Equal(-9m, line.AdjustmentDelta);
    }

    [Fact]
    public async Task Shared_location_requires_specific_approval()
    {
        await using var fixture = await Fixture.CreateAsync();
        var existing = await fixture.AddProductAsync("EXISTING");
        var added = await fixture.AddProductAsync("ADDED");
        var location = await fixture.AddLocationAsync("MIXED");
        fixture.Db.ProductLocationAssignments.Add(new()
        {
            ProductId = existing.Id,
            LocationId = location.Id
        });
        await fixture.Db.SaveChangesAsync();
        var operationId = Guid.NewGuid();

        var conflict = await fixture.Service.ConfirmAsync(new(
            operationId, InventoryMovementType.Entry, fixture.OperatorPin,
            [new(added.Id, 1m, DestinationLocationId: location.Id)]));
        Assert.Equal(InventoryMovementStatus.RequiresLocationSharingConfirmation, conflict.Status);
        Assert.Contains("EXISTING", Assert.Single(conflict.Conflicts).ExistingProductSkus);
        Assert.False(await fixture.Db.InventoryMovements.AnyAsync());

        var confirmed = await fixture.Service.ConfirmAsync(new(
            operationId, InventoryMovementType.Entry, fixture.OperatorPin,
            [new(added.Id, 1m, DestinationLocationId: location.Id)],
            ApprovedSharedAssignments: [new(added.Id, location.Id)]));
        Assert.Equal(InventoryMovementStatus.Success, confirmed.Status);
        Assert.Equal(2, await fixture.Db.ProductLocationAssignments.CountAsync());
    }

    [Fact]
    public async Task Same_operation_is_idempotent_but_changed_payload_conflicts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("IDEMPOTENT");
        var location = await fixture.AddLocationAsync("IDEMPOTENCY");
        var operationId = Guid.NewGuid();
        var command = new InventoryMovementCommand(
            operationId, InventoryMovementType.Entry, fixture.OperatorPin,
            [new(product.Id, 5m, DestinationLocationId: location.Id)]);

        var first = await fixture.Service.ConfirmAsync(command);
        var repeated = await fixture.Service.ConfirmAsync(command);
        var changed = await fixture.Service.ConfirmAsync(command with
        {
            Lines = [new(product.Id, 6m, DestinationLocationId: location.Id)]
        });

        Assert.Equal(InventoryMovementStatus.Success, first.Status);
        Assert.Equal(first.MovementId, repeated.MovementId);
        Assert.Equal(InventoryMovementStatus.IdempotencyConflict, changed.Status);
        Assert.Equal(5m, (await fixture.Db.InventoryBalances.SingleAsync()).Quantity);
        Assert.Equal(1, await fixture.Db.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task Invalid_pin_is_rejected_and_every_product_uses_internal_daily_lots()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("LOT-PRODUCT");
        var location = await fixture.AddLocationAsync("LOT-AREA");
        var command = new InventoryMovementCommand(
            Guid.NewGuid(), InventoryMovementType.Entry, "9999",
            [new(product.Id, 1m, DestinationLocationId: location.Id)]);

        Assert.Equal(InventoryMovementStatus.InvalidPin, (await fixture.Service.ConfirmAsync(command)).Status);
        Assert.Equal(InventoryMovementStatus.Success,
            (await fixture.Service.ConfirmAsync(command with { Pin = fixture.OperatorPin })).Status);
        Assert.Single(await fixture.Db.ProductLots.ToListAsync());
        Assert.Single(await fixture.Db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task Exit_consumes_internal_lot_and_keeps_operator_capture_aggregate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("AUTO-LOT");
        var location = await fixture.AddLocationAsync("AUTO-AREA");
        await fixture.Service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, fixture.OperatorPin,
            [new(product.Id, 5m, DestinationLocationId: location.Id)]));

        var result = await fixture.Service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Exit, fixture.OperatorPin,
            [new(product.Id, 7m, SourceLocationId: location.Id)]));

        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        var movement = await fixture.Db.InventoryMovements.Include(item => item.Lines).ThenInclude(item => item.BalanceChanges)
            .SingleAsync(item => item.Type == InventoryMovementType.Exit);
        Assert.Equal(InventoryLotAllocationMode.AutomaticFefo, movement.Lines.Single().LotAllocationMode);
        Assert.Equal(-7m, movement.Lines.Single().BalanceChanges.Sum(change => change.DeltaQuantity));
        Assert.Equal(-2m, (await fixture.Db.InventoryBalances.Where(item => item.ProductId == product.Id).SumAsync(item => item.Quantity)));
    }

    [Fact]
    public async Task Admin_also_uses_pin_per_operation_and_inactive_operator_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("PIN-ROLES");
        var location = await fixture.AddLocationAsync("PIN-AREA");
        const string adminPin = "1357";
        await fixture.AddUserAsync("Administrador operativo", 1, adminPin);

        var adminResult = await fixture.Service.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, adminPin,
            [new(product.Id, 1m, DestinationLocationId: location.Id)]));
        Assert.Equal(InventoryMovementStatus.Success, adminResult.Status);

        var operatorUser = await fixture.Db.Users.SingleAsync(user => user.Id == fixture.OperatorId);
        operatorUser.IsActive = false;
        await fixture.Db.SaveChangesAsync();
        var inactiveResult = await fixture.Service.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, fixture.OperatorPin,
            [new(product.Id, 1m, DestinationLocationId: location.Id)]));
        Assert.Equal(InventoryMovementStatus.InvalidPin, inactiveResult.Status);
    }

    [Fact]
    public async Task Confirmed_history_is_immutable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("IMMUTABLE");
        var location = await fixture.AddLocationAsync("AUDIT");
        await fixture.Service.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, fixture.OperatorPin,
            [new(product.Id, 1m, DestinationLocationId: location.Id)]));

        var movement = await fixture.Db.InventoryMovements.SingleAsync();
        movement.Notes = "Intento de sobrescritura";

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Admin_correction_reverses_entry_and_keeps_original_immutable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("REVERSAL");
        var location = await fixture.AddLocationAsync("REVERSAL-A");
        var original = await fixture.Service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, fixture.OperatorPin,
            [new(product.Id, 3.5m, DestinationLocationId: location.Id)]));
        var admin = await fixture.AddUserAsync("Administrador", 1, "1357");

        var result = await fixture.CorrectionService.ConfirmAsync(new(Guid.NewGuid(), original.MovementId!.Value, admin.Id, "1357", "Cantidad capturada por error"));

        Assert.Equal(InventoryCorrectionStatus.Success, result.Status);
        Assert.Equal(0m, (await fixture.Db.InventoryBalances.SingleAsync()).Quantity);
        Assert.Equal(2, await fixture.Db.InventoryMovements.CountAsync());
        var correction = await fixture.Db.InventoryMovementCorrections.SingleAsync();
        Assert.Equal(original.MovementId, correction.OriginalMovementId);
        Assert.Equal(admin.Id, correction.RequestedByUserId);
        Assert.Equal(admin.Id, correction.AuthorizedByUserId);
    }

    [Fact]
    public async Task Transfer_consumes_oldest_lots_and_preserves_each_lot_at_destination()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("FEFO-TRANSFER");
        var source = await fixture.AddLocationAsync("FEFO-SOURCE");
        var destination = await fixture.AddLocationAsync("FEFO-DESTINATION");
        var oldest = new ProductLot
        {
            ProductId = product.Id,
            Number = "AUTO-20240101",
            NormalizedNumber = "AUTO-20240101",
            LotDate = new DateOnly(2024, 1, 1),
            CreatedAt = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero)
        };
        fixture.Db.Add(oldest);
        fixture.Db.InventoryBalances.Add(new InventoryBalance
        {
            ProductId = product.Id,
            LocationId = source.Id,
            LotId = oldest.Id,
            Quantity = 2m
        });
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, fixture.OperatorPin,
            [new(product.Id, 3m, DestinationLocationId: source.Id)]));

        var result = await fixture.Service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Transfer, fixture.OperatorPin,
            [new(product.Id, 4m, SourceLocationId: source.Id, DestinationLocationId: destination.Id)]));

        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        var transfer = await fixture.Db.InventoryMovements.Include(item => item.Lines)
            .ThenInclude(item => item.BalanceChanges)
            .SingleAsync(item => item.Id == result.MovementId);
        var changes = transfer.Lines.Single().BalanceChanges;
        Assert.Contains(changes, item => item.LotId == oldest.Id && item.DeltaQuantity == -2m);
        Assert.Contains(changes, item => item.LotId == oldest.Id && item.DeltaQuantity == 2m);
        Assert.All(changes, item => Assert.NotNull(item.LotId));
        Assert.Equal(4m, (await fixture.Db.InventoryBalances.Where(item =>
            item.ProductId == product.Id && item.LocationId == destination.Id).SumAsync(item => item.Quantity)));
    }

    [Fact]
    public async Task Correction_is_idempotent_and_historical_null_lot_is_reversed_to_initial_lot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("HISTORICAL-LOT");
        var location = await fixture.AddLocationAsync("HISTORICAL-AREA");
        var initialLot = new ProductLot
        {
            ProductId = product.Id,
            Number = "AUTO-20240101",
            NormalizedNumber = "AUTO-20240101",
            LotDate = new DateOnly(2024, 1, 1),
            CreatedAt = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero)
        };
        var original = new InventoryMovement
        {
            Type = InventoryMovementType.Entry,
            ResponsibleUserId = fixture.OperatorId,
            OperationId = Guid.NewGuid(),
            RequestFingerprint = new string('A', 64),
            OccurredAt = DateTimeOffset.UtcNow,
            RecordedAt = DateTimeOffset.UtcNow,
            Lines =
            [
                new InventoryMovementLine
                {
                    LineNumber = 1,
                    ProductId = product.Id,
                    UnitId = product.BaseUnitId,
                    Quantity = 3m,
                    DestinationLocationId = location.Id,
                    BalanceChanges =
                    [
                        new InventoryBalanceChange
                        {
                            LocationId = location.Id,
                            DeltaQuantity = 3m,
                            PreviousQuantity = 0m,
                            ResultingQuantity = 3m
                        }
                    ]
                }
            ]
        };
        fixture.Db.AddRange(initialLot, original);
        fixture.Db.InventoryBalances.Add(new InventoryBalance
        {
            ProductId = product.Id,
            LocationId = location.Id,
            LotId = initialLot.Id,
            Quantity = 3m
        });
        var admin = await fixture.AddUserAsync("Administrador histórico", 1, "1357");
        await fixture.Db.SaveChangesAsync();
        var command = new InventoryCorrectionCommand(Guid.NewGuid(), original.Id, admin.Id, "1357", "Corrección histórica");

        var first = await fixture.CorrectionService.ConfirmAsync(command);
        var repeated = await fixture.CorrectionService.ConfirmAsync(command);
        var changed = await fixture.CorrectionService.ConfirmAsync(command with { Reason = "Otro motivo" });

        Assert.Equal(InventoryCorrectionStatus.Success, first.Status);
        Assert.Equal(first.CorrectionId, repeated.CorrectionId);
        Assert.Equal(InventoryCorrectionStatus.IdempotencyConflict, changed.Status);
        Assert.Equal(0m, (await fixture.Db.InventoryBalances.SingleAsync(item => item.LotId == initialLot.Id)).Quantity);
        var reversal = await fixture.Db.InventoryMovements.Include(item => item.Lines)
            .ThenInclude(item => item.BalanceChanges)
            .SingleAsync(item => item.Id == first.ReversalMovementId);
        Assert.All(reversal.Lines.SelectMany(item => item.BalanceChanges), item => Assert.Equal(initialLot.Id, item.LotId));
        Assert.All(await fixture.Db.InventoryBalances.ToListAsync(), item => Assert.NotNull(item.LotId));
    }

    [Fact]
    public async Task Wip_issue_has_no_destination_balance_and_linked_returns_are_bounded_and_idempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("WIP-MATERIAL");
        var rack = await fixture.AddLocationAsync("A-1-8");
        var wip = await fixture.AddLocationAsync("WIP-2", LocationOperationalRole.Wip);
        var returnRack = await fixture.AddLocationAsync("B-1-1");
        await fixture.Service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, fixture.OperatorPin,
            [new(product.Id, 100m, DestinationLocationId: rack.Id)]));

        var issue = await fixture.Service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Exit,
            fixture.OperatorPin, [new(product.Id, 20m, SourceLocationId: rack.Id)], Purpose:
            InventoryMovementPurpose.ProductionIssue, OperationalAreaId: wip.Id));

        Assert.Equal(InventoryMovementStatus.Success, issue.Status);
        Assert.Equal(80m, (await fixture.Db.InventoryBalances.Where(item => item.LocationId == rack.Id)
            .SumAsync(item => item.Quantity)));
        Assert.False(await fixture.Db.InventoryBalances.AnyAsync(item => item.LocationId == wip.Id));
        Assert.False(await fixture.Db.ProductLocationAssignments.AnyAsync(item => item.LocationId == wip.Id));
        var lineId = await fixture.Db.InventoryMovementLines.Where(item => item.MovementId == issue.MovementId)
            .Select(item => item.Id).SingleAsync();
        var pinService = new UserPinService(fixture.Db, new PinProtector(Fixture.LookupKey));
        var dispositions = new WipDispositionService(fixture.Db, pinService, TimeProvider.System);

        var warehouseOperation = Guid.NewGuid();
        var warehouse = await dispositions.ConfirmAsync(new(warehouseOperation, lineId,
            WipDispositionType.WarehouseReturn, 5m, fixture.OperatorPin, returnRack.Id));
        var repeated = await dispositions.ConfirmAsync(new(warehouseOperation, lineId,
            WipDispositionType.WarehouseReturn, 5m, fixture.OperatorPin, returnRack.Id));
        var supplier = await dispositions.ConfirmAsync(new(Guid.NewGuid(), lineId,
            WipDispositionType.SupplierReturn, 3m, fixture.OperatorPin, Reference: "RMA-1"));
        var excessive = await dispositions.ConfirmAsync(new(Guid.NewGuid(), lineId,
            WipDispositionType.SupplierReturn, 13m, fixture.OperatorPin, Reference: "RMA-2"));

        Assert.Equal(WipDispositionStatus.Success, warehouse.Status);
        Assert.Equal(warehouse.DispositionId, repeated.DispositionId);
        Assert.Equal(WipDispositionStatus.Success, supplier.Status);
        Assert.Equal(WipDispositionStatus.ValidationFailed, excessive.Status);
        Assert.Equal(5m, await fixture.Db.InventoryBalances.Where(item => item.LocationId == returnRack.Id)
            .SumAsync(item => item.Quantity));
        Assert.Equal(2, await fixture.Db.WipDispositions.CountAsync());
        Assert.Equal(12m, (await new WipReportService(fixture.Db,
            new WarehouseClock(new WarehouseSettingsService(fixture.Db))).GetIssueAsync(lineId))!.AssumedConsumed);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string LookupKey =
            "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        public string OperatorPin { get; } = "2468";
        public Guid OperatorId { get; }
        public WarehouseDbContext Db { get; }
        public InventoryMovementService Service { get; }
        public InventoryCorrectionService CorrectionService { get; }

        private Fixture(WarehouseDbContext db, InventoryMovementService service, InventoryCorrectionService correctionService, Guid operatorId)
        {
            Db = db;
            Service = service;
            CorrectionService = correctionService;
            OperatorId = operatorId;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            await db.Database.EnsureCreatedAsync();
            var pinService = new UserPinService(db, new PinProtector(LookupKey));
            var user = new User
            {
                FullName = "Operador de inventario",
                RoleId = 2,
                PinLookup = string.Empty,
                PinHash = string.Empty
            };
            Assert.Equal(PinAssignmentResult.Success, await pinService.AssignAsync(user, "2468"));
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var service = new InventoryMovementService(db, pinService, TimeProvider.System);
            return new(db, service, new InventoryCorrectionService(db, pinService, service, TimeProvider.System), user.Id);
        }

        public async Task<User> AddUserAsync(string name, short roleId, string pin)
        {
            var user = new User
            {
                FullName = name,
                RoleId = roleId,
                PinLookup = string.Empty,
                PinHash = string.Empty
            };
            var pinService = new UserPinService(Db, new PinProtector(LookupKey));
            Assert.Equal(PinAssignmentResult.Success, await pinService.AssignAsync(user, pin));
            Db.Users.Add(user);
            await Db.SaveChangesAsync();
            return user;
        }

        public async Task<Product> AddProductAsync(string sku)
        {
            var product = new Product { Sku = sku, BaseUnitId = 1 };
            Db.Products.Add(product);
            await Db.SaveChangesAsync();
            return product;
        }

        public async Task<Location> AddLocationAsync(string code,
            LocationOperationalRole role = LocationOperationalRole.Storage)
        {
            var location = new Location { Code = code, Kind = LocationKind.Area, OperationalRole = role };
            Db.Locations.Add(location);
            await Db.SaveChangesAsync();
            return location;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
