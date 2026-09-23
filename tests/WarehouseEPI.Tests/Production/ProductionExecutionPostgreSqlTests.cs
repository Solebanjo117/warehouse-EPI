using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence.Migrations;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Tests.Inventory;

namespace WarehouseEPI.Tests.Production;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class ProductionExecutionPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task P5_postgresql_single_lot_concurrency_and_closure_are_atomic()
    {
        await using var db = fixture.CreateDbContext();
        var pins = new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey));
        var admin = new User { FullName = "Admin P5", RoleId = 1, PinLookup = "", PinHash = "" };
        var worker = new User { FullName = "Operador P5", RoleId = 2, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(admin, "8612"); await pins.AssignAsync(worker, "8613");
        var product = new Product { Sku = "P5-PT-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), BaseUnitId = 1 };
        var process = new ProductionStage { Code = "P5-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), Name = "Proceso final" };
        var shift = new ProductionShift { Code = "P5-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), Name = "Turno P5" };
        var destination = new Location { Code = "P5-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), Kind = LocationKind.Area };
        var order = new ProductionWorkOrder { Product = product, UnitId = 1, CreatedByUser = admin,
            CreateOperationId = Guid.NewGuid(), CreateFingerprint = "P5", Number = "P5-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            OriginalTargetQuantity = 10, TargetQuantity = 10, AuthorizedQuantity = 10, UsesBatchTraceability = true,
            Status = ProductionWorkOrderStatus.Released };
        order.Stages.Add(new() { SourceStage = process, Sequence = 1, Code = process.Code, Name = process.Name });
        db.AddRange(order, worker, shift, destination); await db.SaveChangesAsync();
        var movement = new InventoryMovementService(db, pins, TimeProvider.System);
        var trace = new ProductionTraceabilityService(db, pins, new ProductionMaterialService(db, pins, movement, TimeProvider.System), TimeProvider.System);
        async Task<ProductionTraceabilityResult> CreateLot()
        {
            await using var concurrent = fixture.CreateDbContext();
            var p = new UserPinService(concurrent, new PinProtector(PostgreSqlInventoryFixture.LookupKey));
            var movements = new InventoryMovementService(concurrent, p, TimeProvider.System);
            return await new ProductionTraceabilityService(concurrent, p, new ProductionMaterialService(concurrent, p, movements, TimeProvider.System), TimeProvider.System)
                .CreateBatchAsync(new(Guid.NewGuid(), order.Id, 10, 0, "8612"));
        }
        var attempts = await Task.WhenAll(CreateLot(), CreateLot());
        Assert.Single(attempts, x => x.Success);
        var batch = await db.ProductionBatches.SingleAsync(x => x.WorkOrderId == order.Id);
        await db.Entry(order).ReloadAsync();
        var stage = order.Stages.Single();
        var result = await trace.RecordResultAsync(new(Guid.NewGuid(), order.Id, batch.Id, stage.Id, shift.Id,
            false, 10, 0, 10, 0, [], order.Version, "Retrabajo total", "8613"));
        Assert.True(result.Success, string.Join(";", result.Errors ?? []));
        var service = new ProductionExecutionService(db, pins, TimeProvider.System);
        var rework = Assert.Single(await service.GetReworkAsync(order.Id));
        var close = new ProductionExecutionCommand(Guid.NewGuid(), order.Id, order.Version, "principal", "8613",
            ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Difference), "Atención posterior", "8612");
        Assert.Equal(ProductionCommandStatus.Success, (await service.ApplyAsync(close)).Status);
        Assert.Equal(ProductionCommandStatus.Success, (await service.ApplyAsync(close)).Status);
        var recovered = await trace.RecordResultAsync(new(Guid.NewGuid(), order.Id, batch.Id, stage.Id, shift.Id,
            true, 10, 10, 0, 0, [], order.Version, "Recuperado", "8613", rework.Id));
        Assert.True(recovered.Success, string.Join(";", recovered.Errors ?? []));
        var production = new ProductionService(db, pins, movement, TimeProvider.System);
        Assert.Equal(ProductionCommandStatus.Success, (await production.ReceiveWarehouseAsync(new(Guid.NewGuid(), order.Id,
            stage.Id, 10, destination.Id, "8613", BatchId: batch.Id, ExpectedVersion: order.Version))).Status);
        Assert.Equal(ProductionWorkOrderStatus.PrincipalClosed, order.Status);
        Assert.Equal(ProductionCommandStatus.Success, (await service.ApplyAsync(new(Guid.NewGuid(), order.Id, order.Version,
            "definitive", "8612", ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Difference), "Conciliado"))).Status);
        Assert.Equal(ProductionWorkOrderStatus.Closed, order.Status);
        Assert.Single(await db.ProductLots.Where(x => x.Id == batch.FinishedProductLotId).ToListAsync());
        Assert.Equal(10, await db.InventoryBalances.Where(x => x.ProductId == product.Id).SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task P5_backfill_reconstructs_unambiguous_history_and_rejects_ambiguity()
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var pins = new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey));
        var user = new User { FullName = "Histórico P5", RoleId = 1, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(user, "8614");
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var product = new Product { Sku = "H-P5-" + suffix, BaseUnitId = 1 };
        var process = new ProductionStage { Code = "H-P5-" + suffix, Name = "Histórico" };
        var shift = new ProductionShift { Code = "H-P5-" + suffix, Name = "Histórico" };
        var order = new ProductionWorkOrder { Product = product, UnitId = 1, CreatedByUser = user,
            CreateOperationId = Guid.NewGuid(), CreateFingerprint = "history", Number = "H-P5-" + suffix,
            TargetQuantity = 10, AuthorizedQuantity = 10, UsesBatchTraceability = true, Status = ProductionWorkOrderStatus.InProgress };
        var stage = new ProductionWorkOrderStage { SourceStage = process, Sequence = 1, Code = process.Code, Name = process.Name };
        order.Stages.Add(stage);
        var batch = new ProductionBatch { WorkOrder = order, Number = order.Number + "-L001", CreateOperationId = Guid.NewGuid(),
            CreateFingerprint = "history", AssignedQuantity = 10, CreatedByUser = user,
            FinishedProductLot = new ProductLot { Product = product, Number = order.Number, NormalizedNumber = order.Number.ToUpperInvariant() } };
        var origin = new ProductionBatchResult { Batch = batch, WorkOrderStage = stage, Shift = shift, ResponsibleUser = user,
            OperationId = Guid.NewGuid(), RequestFingerprint = "history", InputQuantity = 10, ReworkQuantity = 10,
            RecordedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        var attempt = new ProductionBatchResult { Batch = batch, WorkOrderStage = stage, Shift = shift, ResponsibleUser = user,
            OperationId = Guid.NewGuid(), RequestFingerprint = "history", IsRework = true, InputQuantity = 10, GoodQuantity = 4, ReworkQuantity = 6,
            RecordedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        db.AddRange(origin, attempt); await db.SaveChangesAsync();
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(typeof(Phase135ProductionExecution).GetTypeInfo(), db.Database.ProviderName!);
        var sql = migration.UpOperations.OfType<SqlOperation>().Single(x => x.Sql.Contains("UPDATE production_work_orders", StringComparison.Ordinal)).Sql;
        await transaction.CreateSavepointAsync("before_backfill");
        await db.Database.ExecuteSqlRawAsync(sql);
        var recovered = await db.ProductionReworkCases.AsNoTracking().SingleAsync(x => x.WorkOrderId == order.Id);
        Assert.Equal(origin.Id, recovered.OriginResultId);
        Assert.Equal(origin.RecordedAt.ToUnixTimeSeconds(), recovered.OriginAt.ToUnixTimeSeconds());
        Assert.Equal(attempt.Id, (await db.ProductionReworkAttempts.SingleAsync(x => x.ReworkCaseId == recovered.Id)).ResultId);
        await transaction.RollbackToSavepointAsync("before_backfill");
        db.ChangeTracker.Clear();
        db.ProductionBatchResults.Add(new() { BatchId = batch.Id, WorkOrderStageId = stage.Id, ShiftId = shift.Id,
            ResponsibleUserId = user.Id, OperationId = Guid.NewGuid(), RequestFingerprint = "ambiguous",
            InputQuantity = 2, ReworkQuantity = 2, RecordedAt = origin.RecordedAt.AddHours(1) });
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Contains("orígenes posibles", error.MessageText);
        await transaction.RollbackAsync();
    }
}
