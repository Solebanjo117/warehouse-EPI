using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Locations;

public sealed record WarehouseMapProduct(Guid ProductId, string Sku, string? Description, string Unit, decimal Quantity, bool IsAssigned);
public sealed record WarehouseMapPosition(Guid LocationId, string Code, short? PalletNumber, string? Description, LocationOperationalRole OperationalRole, bool IsActive, bool IsBlocked, string? BlockReason, int AssignmentCount, int ProductCount, bool HasInventory, bool HasNegative, IReadOnlyList<WarehouseMapProduct> Products);
public sealed record WarehouseMapElementView(Guid Id, string Kind, string Label, string? RowCode, short? RackNumber, Guid? LocationId, decimal X, decimal Y, decimal Width, decimal Height, short Rotation, int ZIndex, bool IsVisible, IReadOnlyList<WarehouseMapPosition> Positions)
{
    public bool IsWip => Positions.Any(position => position.OperationalRole == LocationOperationalRole.Wip);
}
public sealed record WarehouseMapView(int Version, uint RowVersion, bool IsInitialized, IReadOnlyList<WarehouseMapElementView> Elements, IReadOnlyList<WarehouseMapElementView> Unplaced, int Available, int Blocked, int Inactive, int WithInventory, int Negative);
public sealed record WarehouseMapGeometry(Guid Id, decimal X, decimal Y, decimal Width, decimal Height, short Rotation, int ZIndex, bool IsVisible);
public sealed record WarehouseMapSaveCommand(Guid OperationId, Guid RequestedByUserId, string Pin, string? Reason, IReadOnlyList<WarehouseMapGeometry> Elements);
public enum WarehouseMapSaveStatus { Success, InvalidPin, Unauthorized, ValidationFailed, Conflict, IdempotencyConflict, NotInitialized }
public sealed record WarehouseMapSaveResult(WarehouseMapSaveStatus Status, int Version = 0, IReadOnlyList<string>? Errors = null) { public IReadOnlyList<string> ValidationErrors => Errors ?? []; }
public sealed record WarehouseMapRevisionView(Guid Id, int PreviousVersion, int NewVersion, string? Reason, string RequestedBy, string AuthorizedBy, DateTimeOffset RecordedAt);

public sealed class WarehouseMapService(WarehouseDbContext dbContext, UserPinService? pins = null, TimeProvider? timeProvider = null)
{
    public const decimal CanvasWidth = 1600m;
    public const decimal CanvasHeight = 900m;

    public async Task<WarehouseMapView> GetAsync(bool includeProposal, CancellationToken token = default)
    {
        var layout = await dbContext.WarehouseMapLayouts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == 1, token);
        var stored = layout is null ? [] : await dbContext.WarehouseMapElements.AsNoTracking().Where(item => item.LayoutId == 1).OrderBy(item => item.ZIndex).ToListAsync(token);
        var proposal = includeProposal ? await BuildProposalAsync(token) : [];
        var elements = stored.Count == 0
            ? proposal
            : includeProposal
                ? stored.Concat(proposal.Where(item => stored.All(saved => saved.Id != item.Id)).Select(item =>
                    {
                        item.IsVisible = false;
                        return item;
                    }))
                    .OrderBy(item => item.ZIndex).ToList()
                : stored;
        var locations = await LoadPositionsAsync(token);
        var views = elements.Select(item => ToView(item, locations)).ToArray();
        return new(layout?.Version ?? 0, layout?.RowVersion ?? 0, layout is not null && stored.Count != 0, views.Where(item => item.IsVisible).ToArray(), views.Where(item => !item.IsVisible).ToArray(), locations.Values.Count(item => item.IsActive && !item.IsBlocked), locations.Values.Count(item => item.IsActive && item.IsBlocked), locations.Values.Count(item => !item.IsActive), locations.Values.Count(item => item.HasInventory), locations.Values.Count(item => item.HasNegative));
    }

    public async Task<IReadOnlyList<WarehouseMapRevisionView>> GetRevisionsAsync(int take = 20, CancellationToken token = default) =>
        await dbContext.WarehouseMapRevisions.AsNoTracking().OrderByDescending(item => item.RecordedAt).Take(Math.Clamp(take, 1, 100))
            .Select(item => new WarehouseMapRevisionView(item.Id, item.PreviousVersion, item.NewVersion, item.Reason, item.RequestedByUser.FullName, item.AuthorizedByUser.FullName, item.RecordedAt)).ToListAsync(token);

    public async Task<WarehouseMapSaveResult> InitializeAsync(Guid operationId, Guid requestedByUserId, string pin, string? reason, IReadOnlyList<WarehouseMapGeometry>? geometry = null, CancellationToken token = default)
    {
        var proposal = await BuildProposalAsync(token);
        var geometries = geometry?.ToArray() ?? proposal.Select(ToGeometry).ToArray();
        var byId = proposal.ToDictionary(item => item.Id);
        if (geometries.Length == byId.Count && geometries.All(item => byId.ContainsKey(item.Id))) foreach (var item in geometries) { var target = byId[item.Id]; target.X = item.X; target.Y = item.Y; target.Width = item.Width; target.Height = item.Height; target.Rotation = item.Rotation; target.ZIndex = item.ZIndex; target.IsVisible = item.IsVisible; }
        return await SaveCoreAsync(operationId, requestedByUserId, pin, reason, geometries, true, token, proposal);
    }

    public Task<WarehouseMapSaveResult> SaveAsync(WarehouseMapSaveCommand command, CancellationToken token = default) =>
        SaveCoreAsync(command.OperationId, command.RequestedByUserId, command.Pin, command.Reason, command.Elements, false, token);

    private async Task<WarehouseMapSaveResult> SaveCoreAsync(Guid operationId, Guid requestedByUserId, string pin, string? reason, IReadOnlyList<WarehouseMapGeometry> geometries, bool initialize, CancellationToken token, List<WarehouseMapElement>? initialElements = null)
    {
        var errors = Validate(operationId, requestedByUserId, reason, geometries);
        if (errors.Count != 0) return new(WarehouseMapSaveStatus.ValidationFailed, Errors: errors);
        var requester = await dbContext.Users.AsNoTracking().Include(item => item.Role).SingleOrDefaultAsync(item => item.Id == requestedByUserId, token);
        if (requester is null || !requester.IsActive || requester.Role.Code != "ADMIN") return new(WarehouseMapSaveStatus.Unauthorized);
        if (pins is null) return new(WarehouseMapSaveStatus.Unauthorized);
        var authorized = await pins.AuthenticateAsync(pin, token);
        if (authorized is null || authorized.Role.Code != "ADMIN") return new(WarehouseMapSaveStatus.InvalidPin);
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var payload = JsonSerializer.Serialize(geometries.OrderBy(item => item.Id));
        var fingerprint = Hash($"{requestedByUserId:N}|{authorized.Id:N}|{normalizedReason}|{payload}");
        var existingRevision = await dbContext.WarehouseMapRevisions.AsNoTracking().SingleOrDefaultAsync(item => item.OperationId == operationId, token);
        if (existingRevision is not null) return new(existingRevision.RequestFingerprint == fingerprint ? WarehouseMapSaveStatus.Success : WarehouseMapSaveStatus.IdempotencyConflict, existingRevision.NewVersion);
        await using var transaction = dbContext.Database.IsRelational() ? await dbContext.Database.BeginTransactionAsync(token) : null;
        var useDirectUpdates = !initialize && dbContext.Database.IsRelational();
        if (useDirectUpdates && dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            await dbContext.Database.ExecuteSqlRawAsync("SELECT id FROM warehouse_map_layouts WHERE id = 1 FOR UPDATE", token);
        var layout = !useDirectUpdates
            ? await dbContext.WarehouseMapLayouts.Include(item => item.Elements).SingleOrDefaultAsync(item => item.Id == 1, token)
            : await dbContext.WarehouseMapLayouts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == 1, token);
        if (initialize)
        {
            if (layout is not null) return new(WarehouseMapSaveStatus.Conflict, layout.Version);
            layout = new WarehouseMapLayout { Id = 1, Version = 0 };
            dbContext.WarehouseMapLayouts.Add(layout);
            var proposal = initialElements ?? await BuildProposalAsync(token);
            foreach (var item in proposal) layout.Elements.Add(item);
        }
        else if (layout is null) return new(WarehouseMapSaveStatus.NotInitialized);
        else if (useDirectUpdates) layout.Elements = await dbContext.WarehouseMapElements.AsNoTracking().Where(item => item.LayoutId == 1).ToListAsync(token);
        var byId = layout.Elements.ToDictionary(item => item.Id);
        if (byId.Keys.Except(geometries.Select(item => item.Id)).Any())
            return new(WarehouseMapSaveStatus.ValidationFailed, layout.Version, ["La lista de elementos ya no coincide con el croquis actual."]);

        var catalog = await BuildProposalAsync(token);
        var catalogById = catalog.ToDictionary(item => item.Id);
        var additions = geometries.Where(item => !byId.ContainsKey(item.Id)).ToArray();
        var additionIds = additions.Select(item => item.Id).ToHashSet();
        if (additions.Any(item => !catalogById.ContainsKey(item.Id)))
            return new(WarehouseMapSaveStatus.ValidationFailed, layout.Version, ["El croquis contiene elementos que no existen en el catálogo actual."]);
        foreach (var addition in additions)
        {
            var element = catalogById[addition.Id];
            layout.Elements.Add(element);
            byId.Add(element.Id, element);
        }
        var before = layout.Elements.OrderBy(item => item.Id).Select(ToGeometry).ToArray();
        foreach (var geometry in geometries)
        {
            var item = byId[geometry.Id]; item.X = geometry.X; item.Y = geometry.Y; item.Width = geometry.Width; item.Height = geometry.Height; item.Rotation = geometry.Rotation; item.ZIndex = geometry.ZIndex; item.IsVisible = geometry.IsVisible;
        }
        var previousVersion = layout.Version;
        var newVersion = previousVersion + 1;
        var recordedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var changes = JsonSerializer.Serialize(new { Before = before, After = geometries.OrderBy(item => item.Id) });
        if (!useDirectUpdates)
        {
            layout.Version = newVersion;
            layout.UpdatedAt = recordedAt;
            layout.UpdatedByUserId = authorized.Id;
        }
        else
        {
            foreach (var geometry in geometries.Where(item => !additionIds.Contains(item.Id)))
            {
                var affected = await dbContext.WarehouseMapElements.Where(item => item.Id == geometry.Id).ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.X, geometry.X)
                    .SetProperty(item => item.Y, geometry.Y)
                    .SetProperty(item => item.Width, geometry.Width)
                    .SetProperty(item => item.Height, geometry.Height)
                    .SetProperty(item => item.Rotation, geometry.Rotation)
                    .SetProperty(item => item.ZIndex, geometry.ZIndex)
                    .SetProperty(item => item.IsVisible, geometry.IsVisible), token);
                if (affected != 1) return new(WarehouseMapSaveStatus.ValidationFailed, previousVersion,
                    ["Uno de los elementos del croquis ya no existe."]);
            }
            if (additionIds.Count != 0) dbContext.WarehouseMapElements.AddRange(additionIds.Select(id => byId[id]));
            var layoutAffected = await dbContext.WarehouseMapLayouts.Where(item => item.Id == 1).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Version, newVersion)
                .SetProperty(item => item.UpdatedAt, recordedAt)
                .SetProperty(item => item.UpdatedByUserId, authorized.Id), token);
            if (layoutAffected != 1) return new(WarehouseMapSaveStatus.NotInitialized);
        }
        dbContext.WarehouseMapRevisions.Add(new WarehouseMapRevision { OperationId = operationId, RequestFingerprint = fingerprint, PreviousVersion = previousVersion, NewVersion = newVersion, Reason = normalizedReason, ChangesJson = changes, RequestedByUserId = requester.Id, AuthorizedByUserId = authorized.Id, RecordedAt = recordedAt });
        try { await dbContext.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            var currentVersion = await dbContext.WarehouseMapLayouts.AsNoTracking().Where(item => item.Id == 1).Select(item => item.Version).SingleOrDefaultAsync(token);
            return new(WarehouseMapSaveStatus.ValidationFailed, currentVersion,
                ["No fue posible guardar el croquis. Vuelve a intentarlo."]);
        }
        if (transaction is not null) await transaction.CommitAsync(token);
        return new(WarehouseMapSaveStatus.Success, newVersion);
    }

    private async Task<List<WarehouseMapElement>> BuildProposalAsync(CancellationToken token)
    {
        var rackKeys = await dbContext.Locations.AsNoTracking()
            .Where(item => item.Kind == LocationKind.Rack && item.RowCode != null && item.RackNumber != null)
            .Select(item => new { item.RowCode, item.RackNumber })
            .Distinct()
            .OrderBy(item => item.RowCode)
            .ThenBy(item => item.RackNumber)
            .ToListAsync(token);
        var areas = await dbContext.Locations.AsNoTracking().Where(item => item.Kind == LocationKind.Area).OrderBy(item => item.Code).ToListAsync(token);
        var result = new List<WarehouseMapElement>(); var z = 10;
        var rows = rackKeys.GroupBy(item => item.RowCode!).ToDictionary(item => item.Key,
            item => item.Select(value => value.RackNumber!.Value).OrderBy(value => value).ToArray());
        foreach (var (row, racks) in rows) foreach (var rack in racks)
        {
            var placed = TryRowAnchor(row, out var anchor);
            var index = Array.IndexOf(racks, rack); var x = anchor.X + (anchor.Reverse ? racks.Length - 1 - index : index) * anchor.Step;
            result.Add(new WarehouseMapElement { Id = StableId($"RACK|{row}|{rack}"), Kind = WarehouseMapElementKind.Rack, RowCode = row, RackNumber = rack, X = placed ? x : 40 + index * 62, Y = placed ? anchor.Y : 830, Width = anchor.Width, Height = anchor.Height, Rotation = anchor.Rotation, ZIndex = z++, IsVisible = placed });
        }
        var unknownAreaIndex = 0;
        foreach (var area in areas)
        {
            var geometry = AreaGeometry(area.Code, unknownAreaIndex++);
            result.Add(new WarehouseMapElement { Id = StableId($"AREA|{area.Id:N}"), Kind = WarehouseMapElementKind.Area, LocationId = area.Id, X = geometry.X, Y = geometry.Y, Width = geometry.Width, Height = geometry.Height, Rotation = geometry.Rotation, ZIndex = z++, IsVisible = geometry.Placed });
        }
        return result;
    }

    private async Task<Dictionary<Guid, WarehouseMapPosition>> LoadPositionsAsync(CancellationToken token)
    {
        var baseRows = await dbContext.Locations.AsNoTracking().Select(item => new { item.Id, item.Code, item.PalletNumber, item.Description, item.OperationalRole, item.IsActive, item.IsBlocked, item.BlockReason }).ToListAsync(token);
        // Keep the database queries simple here. PostgreSQL cannot translate the previous
        // aggregate projection reliably once Product.BaseUnit is joined inside GroupBy.
        // A warehouse map is an administrative view, so aggregate the already materialized
        // rows without mixing quantities from different units.
        var assignments = await dbContext.ProductLocationAssignments.AsNoTracking()
            .Where(item => item.IsActive)
            .Include(item => item.Product)
            .ThenInclude(product => product.BaseUnit)
            .ToListAsync(token);
        var balances = await dbContext.InventoryBalances.AsNoTracking()
            .Include(item => item.Product)
            .ThenInclude(product => product.BaseUnit)
            .ToListAsync(token);
        var assignedSet = assignments.Select(item => (item.LocationId, item.ProductId)).ToHashSet();
        var balanceProducts = balances.Where(item => item.Quantity != 0)
            .GroupBy(item => new { item.LocationId, item.ProductId, item.Product.Sku, item.Product.Description, Unit = item.Product.BaseUnit.Code })
            .Select(group => new
            {
                group.Key.LocationId,
                Product = new WarehouseMapProduct(group.Key.ProductId, group.Key.Sku, group.Key.Description,
                    group.Key.Unit, group.Sum(item => item.Quantity), assignedSet.Contains((group.Key.LocationId, group.Key.ProductId)))
            });
        var productsWithBalance = balanceProducts.Select(item => (item.LocationId, item.Product.ProductId)).ToHashSet();
        var assignedProducts = assignments.Where(item => !productsWithBalance.Contains((item.LocationId, item.ProductId)))
            .Select(item => new
            {
                item.LocationId,
                Product = new WarehouseMapProduct(item.ProductId, item.Product.Sku, item.Product.Description,
                    item.Product.BaseUnit.Code, 0, true)
            });
        var products = balanceProducts.Concat(assignedProducts).GroupBy(item => item.LocationId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<WarehouseMapProduct>)group.Select(item => item.Product).ToArray());
        return baseRows.ToDictionary(item => item.Id, item => { var list = products.GetValueOrDefault(item.Id) ?? []; return new WarehouseMapPosition(item.Id, item.Code, item.PalletNumber, item.Description, item.OperationalRole, item.IsActive, item.IsBlocked, item.BlockReason, assignments.Count(value => value.LocationId == item.Id), list.Count(value => value.Quantity != 0), list.Any(value => value.Quantity != 0), list.Any(value => value.Quantity < 0), list); });
    }

    private static WarehouseMapElementView ToView(WarehouseMapElement item, IReadOnlyDictionary<Guid, WarehouseMapPosition> locations)
    {
        var positions = item.Kind == WarehouseMapElementKind.Area ? (item.LocationId is Guid id && locations.TryGetValue(id, out var area) ? [area] : []) : locations.Values.Where(value => value.Code.StartsWith($"{item.RowCode}-{item.RackNumber}-", StringComparison.Ordinal)).OrderByDescending(value => value.PalletNumber is 7 or 8 or 9).ThenBy(value => value.PalletNumber).ToArray();
        return new(item.Id, item.Kind.ToString(), item.Kind == WarehouseMapElementKind.Rack ? $"{item.RowCode}-{item.RackNumber}" : positions.FirstOrDefault()?.Code ?? "Área", item.RowCode, item.RackNumber, item.LocationId, item.X, item.Y, item.Width, item.Height, item.Rotation, item.ZIndex, item.IsVisible, positions);
    }

    private static List<string> Validate(Guid operationId, Guid userId, string? reason, IReadOnlyList<WarehouseMapGeometry> elements)
    {
        var errors = new List<string>(); if (operationId == Guid.Empty || userId == Guid.Empty) errors.Add("La operación y el solicitante son obligatorios."); if ((reason?.Trim().Length ?? 0) > 500) errors.Add("El motivo admite hasta 500 caracteres."); if (elements.Count is 0 or > 1000 || elements.Select(item => item.Id).Distinct().Count() != elements.Count) errors.Add("La colección de elementos no es válida.");
        if (elements.Any(item => item.X < 0 || item.Y < 0 || item.Width < 10 || item.Height < 10 || item.X + item.Width > CanvasWidth || item.Y + item.Height > CanvasHeight || item.Rotation is not (0 or 90 or 180 or 270))) errors.Add("La geometría debe permanecer dentro del croquis y usar rotaciones de 90°."); return errors;
    }

    private static WarehouseMapGeometry ToGeometry(WarehouseMapElement item) => new(item.Id, item.X, item.Y, item.Width, item.Height, item.Rotation, item.ZIndex, item.IsVisible);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    private static bool TryRowAnchor(string row, out (decimal X, decimal Y, decimal Step, decimal Width, decimal Height, short Rotation, bool Reverse) value)
    {
        var y = new Dictionary<string, decimal> { ["A"] = 100, ["B"] = 175, ["C"] = 210, ["D"] = 300, ["E"] = 335, ["F"] = 415, ["G"] = 450, ["H"] = 530, ["I"] = 565, ["J"] = 645, ["K"] = 680, ["L"] = 755, ["M"] = 790, ["N"] = 425, ["O"] = 510, ["P"] = 550, ["Q"] = 640, ["R"] = 680, ["S"] = 735, ["T"] = 430 };
        if (!y.TryGetValue(row, out var rowY)) { value = default; return false; }
        var side = row is "N" or "O" or "P" or "Q" or "R" or "S"; var vertical = row == "T";
        value = vertical ? (185m, rowY, 42m, 34m, 60m, (short)90, false) : side ? (1270m, rowY, 48m, 44m, 28m, (short)0, false) : (310m, rowY, 56m, 50m, 28m, (short)0, row == "A"); return true;
    }
    private static (decimal X, decimal Y, decimal Width, decimal Height, short Rotation, bool Placed) AreaGeometry(string code, int index)
    {
        var key = code.ToUpperInvariant();
        if (key.Contains("SHIPPING")) return (1370, 340, 120, 90, 0, true);
        if (key.Contains("CARTON")) return (1290, 760, 170, 45, 0, true);
        if (key.Contains("PACK")) return (1080, 805, 180, 65, 0, true);
        if (key.Contains("KPA")) return (100, 85, 190, 90, 0, true);
        if (key.Contains("FC") && key.Contains("ROLL")) return (760, 60, 140, 38, 0, true);
        if (key.Contains("WIP")) return (210 + index * 20, 760, 120, 40, 0, true);
        return (40 + index * 65, 840, 60, 35, 0, false);
    }
}
