using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Inventory;

internal sealed class InventoryMovementStore(WarehouseDbContext dbContext, TimeProvider timeProvider)
{
    internal async Task<List<SharedLocationConflict>> FindSharingConflictsAsync(
        IReadOnlyCollection<InventoryAssignmentKey> pairs,
        IReadOnlyDictionary<Guid, Product> products,
        IReadOnlyDictionary<Guid, Location> locations,
        IReadOnlyCollection<SharedAssignmentApproval> approvals,
        CancellationToken cancellationToken)
    {
        var locationIds = pairs.Select(pair => pair.LocationId).Distinct().ToArray();
        var assignments = await dbContext.ProductLocationAssignments.AsNoTracking()
            .Include(assignment => assignment.Product)
            .Where(assignment => locationIds.Contains(assignment.LocationId))
            .ToListAsync(cancellationToken);
        var occupiedBalances = await dbContext.InventoryBalances.AsNoTracking()
            .Include(balance => balance.Product)
            .Where(balance => locationIds.Contains(balance.LocationId) && balance.Quantity != 0)
            .ToListAsync(cancellationToken);
        var approved = approvals.Select(item => new InventoryAssignmentKey(item.ProductId, item.LocationId)).ToHashSet();
        var conflicts = new List<SharedLocationConflict>();

        foreach (var pair in pairs)
        {
            var sameAssignmentExists = assignments.Any(assignment =>
                assignment.ProductId == pair.ProductId && assignment.LocationId == pair.LocationId);
            var sameProductHasStock = occupiedBalances.Any(balance =>
                balance.ProductId == pair.ProductId && balance.LocationId == pair.LocationId);
            if (sameAssignmentExists || sameProductHasStock || approved.Contains(pair))
                continue;

            var otherSkus = assignments
                .Where(assignment => assignment.LocationId == pair.LocationId && assignment.IsActive &&
                    assignment.ProductId != pair.ProductId)
                .Select(assignment => assignment.Product.Sku)
                .Concat(occupiedBalances.Where(balance => balance.LocationId == pair.LocationId &&
                        balance.ProductId != pair.ProductId)
                    .Select(balance => balance.Product.Sku))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (otherSkus.Length == 0)
                continue;

            conflicts.Add(new(
                pair.ProductId,
                products[pair.ProductId].Sku,
                pair.LocationId,
                locations[pair.LocationId].Code,
                otherSkus));
        }

        return conflicts;
    }

    internal async Task EnsureBalancesExistAsync(
        IReadOnlyCollection<InventoryBalanceKey> keys,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsRelational())
        {
            foreach (var key in keys)
            {
                var id = Guid.NewGuid();
                var now = timeProvider.GetUtcNow();
                await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO inventory_balances (id, product_id, location_id, lot_id, quantity, updated_at)
                    VALUES ({{id}}, {{key.ProductId}}, {{key.LocationId}}, {{key.LotId}}, 0, {{now}})
                    ON CONFLICT DO NOTHING
                    """, cancellationToken);
            }

            return;
        }

        foreach (var key in keys)
        {
            if (!await dbContext.InventoryBalances.AnyAsync(balance =>
                    balance.ProductId == key.ProductId && balance.LocationId == key.LocationId &&
                    balance.LotId == key.LotId, cancellationToken))
            {
                dbContext.InventoryBalances.Add(new InventoryBalance
                {
                    ProductId = key.ProductId,
                    LocationId = key.LocationId,
                    LotId = key.LotId,
                    Quantity = 0m,
                    UpdatedAt = timeProvider.GetUtcNow()
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal async Task<Dictionary<InventoryBalanceKey, InventoryBalance>> LoadTrackedBalancesAsync(
        IReadOnlyCollection<InventoryBalanceKey> keys,
        CancellationToken cancellationToken)
    {
        var productIds = keys.Select(key => key.ProductId).Distinct().ToArray();
        var locationIds = keys.Select(key => key.LocationId).Distinct().ToArray();
        return await dbContext.InventoryBalances
            .Where(item => productIds.Contains(item.ProductId) && locationIds.Contains(item.LocationId) && item.LotId != null)
            .ToDictionaryAsync(item => new InventoryBalanceKey(item.ProductId, item.LocationId, item.LotId), cancellationToken);
    }

    internal async Task UpsertAssignmentsAsync(
        IReadOnlyCollection<InventoryAssignmentKey> pairs,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsRelational())
        {
            foreach (var pair in pairs)
            {
                var now = timeProvider.GetUtcNow();
                await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO product_location_assignments (product_id, location_id, is_active, created_at, updated_at)
                    VALUES ({{pair.ProductId}}, {{pair.LocationId}}, TRUE, {{now}}, {{now}})
                    ON CONFLICT (product_id, location_id) DO UPDATE
                    SET is_active = TRUE,
                        updated_at = CASE
                            WHEN product_location_assignments.is_active = FALSE THEN EXCLUDED.updated_at
                            ELSE product_location_assignments.updated_at
                        END
                    """, cancellationToken);
            }

            return;
        }

        foreach (var pair in pairs)
        {
            var assignment = await dbContext.ProductLocationAssignments.FindAsync(
                [pair.ProductId, pair.LocationId], cancellationToken);
            if (assignment is null)
            {
                dbContext.ProductLocationAssignments.Add(new ProductLocationAssignment
                {
                    ProductId = pair.ProductId,
                    LocationId = pair.LocationId
                });
            }
            else if (!assignment.IsActive)
            {
                assignment.IsActive = true;
                assignment.UpdatedAt = timeProvider.GetUtcNow();
            }
        }
    }

    internal static async Task LockBalancesAsync(
        IReadOnlyCollection<InventoryBalanceKey> keys,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        var productIds = keys.Select(key => key.ProductId).Distinct().Order().ToArray();
        var locationIds = keys.Select(key => key.LocationId).Distinct().Order().ToArray();
        await using var command = CreateCommand(transaction, """
            SELECT id
            FROM inventory_balances
            WHERE product_id = ANY (@product_ids)
              AND location_id = ANY (@location_ids)
            ORDER BY product_id, location_id
            FOR UPDATE
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("product_ids", productIds)
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid
        });
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("location_ids", locationIds)
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid
        });
        await DrainAsync(command, cancellationToken);
    }

    internal static async Task LockLocationsAsync(
        Guid[] ids,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
            return;

        await using var command = CreateCommand(transaction, """
            SELECT id FROM locations
            WHERE id = ANY (@ids)
            ORDER BY id
            FOR UPDATE
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", ids)
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid
        });
        await DrainAsync(command, cancellationToken);
    }

    internal async Task<InventoryMovementResult?> GetExistingResultAsync(
        Guid operationId,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var movement = await dbContext.InventoryMovements.AsNoTracking()
            .Include(item => item.ResponsibleUser)
            .Include(item => item.Lines)
                .ThenInclude(line => line.BalanceChanges)
            .SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken);
        if (movement is null)
            return null;
        if (!string.Equals(movement.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            return new(InventoryMovementStatus.IdempotencyConflict);

        var keys = movement.Lines.SelectMany(line => line.BalanceChanges.Select(change =>
            new InventoryBalanceKey(line.ProductId, change.LocationId, change.LotId))).Distinct().ToArray();
        var currentBalances = keys.Length == 0
            ? []
            : await LoadBalanceResultsAsync(keys, cancellationToken);
        return new(
            InventoryMovementStatus.Success,
            movement.Id,
            movement.ResponsibleUserId,
            movement.ResponsibleUser.FullName,
            currentBalances);
    }

    internal static bool IsOperationIdConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgresException &&
        postgresException.SqlState == PostgresErrorCodes.UniqueViolation &&
        string.Equals(
            postgresException.ConstraintName,
            "IX_inventory_movements_operation_id",
            StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<InventoryBalanceResult>> LoadBalanceResultsAsync(
        IReadOnlyCollection<InventoryBalanceKey> keys,
        CancellationToken cancellationToken)
    {
        var productIds = keys.Select(key => key.ProductId).Distinct().ToArray();
        var locationIds = keys.Select(key => key.LocationId).Distinct().ToArray();
        var balances = await dbContext.InventoryBalances.AsNoTracking()
            .Where(balance => productIds.Contains(balance.ProductId) && locationIds.Contains(balance.LocationId))
            .Select(balance => new InventoryBalanceResult(
                balance.ProductId,
                balance.LocationId,
                balance.LotId,
                balance.Quantity,
                balance.Version,
                balance.Quantity < 0))
            .ToListAsync(cancellationToken);
        return balances.Where(balance => keys.Contains(new(
            balance.ProductId,
            balance.LocationId,
            balance.LotId))).ToArray();
    }

    private static DbCommand CreateCommand(IDbContextTransaction transaction, string sql)
    {
        var command = transaction.GetDbTransaction().Connection!.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = sql;
        return command;
    }

    private static async Task DrainAsync(DbCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) { }
    }
}
