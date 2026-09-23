using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Production;

public enum ProductionPlanningStatus { Success, InvalidPin, NotFound, ValidationFailed, ConcurrencyConflict, IdempotencyConflict }
public sealed record ProductionPlanningResult(ProductionPlanningStatus Status, IReadOnlyList<string>? Errors = null);
public sealed record ProductionPlanningTargetInput(Guid PlanId, string? TargetKey);
public sealed record ReviewProductionPlanningCommand(Guid OperationId, Guid WorkOrderId, uint ExpectedVersion,
    IReadOnlyList<ProductionPlanningTargetInput> Targets, string Reason, string Pin);
public sealed record ProductionPlanningRow(Guid PlanId, Guid StageId, int Sequence, string Stage, string MaterialSku,
    string? MaterialDescription, decimal Planned, string Unit, string TargetKey, string Target, string TargetType,
    string ResolutionSource, bool IsResolved, bool IsAvailable, decimal WarehouseAvailable, decimal WipAvailable,
    IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings);
public sealed record ProductionPlanningView(IReadOnlyList<ProductionPlanningRow> Rows,
    IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings, bool IsLegacyWithoutSnapshot)
{
    public bool CanRelease => Blockers.Count == 0;
}
public sealed record ProductRecipeCatalogLine(Guid MaterialProductId, string MaterialSku, string? MaterialDescription,
    decimal Quantity, string Unit, int Sequence, string Process, string Target, string ResolutionSource,
    string Status, string? Warning);
public sealed record ProductRecipeCatalogSummary(Guid ProductId, string ProductSku, string? ProductDescription,
    string ProductUnit, bool HasRecipe, bool IsComplete, int? Version, decimal? BaseQuantity, string? Route,
    IReadOnlyList<ProductRecipeCatalogLine> Lines);

public sealed class ProductionPlanningService(WarehouseDbContext db, UserPinService pins, TimeProvider timeProvider)
{
    public Task<IReadOnlyList<WipDefaultChoice>> SearchTargetsAsync(Guid stageId, string? search,
        CancellationToken token = default) => new ProductionWipDefaultService(db, pins, timeProvider).SearchAsync(stageId, search, token);

    public async Task ResolveNewOrderAsync(ProductionWorkOrder order, CancellationToken token = default)
    {
        var unresolved = order.MaterialPlan.Where(x => x.WipTargetKind is null).ToArray();
        var catalog = await LoadResolutionCatalogAsync(unresolved.Select(x => x.MaterialProductId),
            unresolved.Select(x => x.WorkOrderStage.SourceStageId), token);
        foreach (var plan in unresolved)
        {
            var target = ResolveDefault(catalog, plan.MaterialProductId, plan.WorkOrderStage.SourceStageId);
            if (target.IsAvailable && target.Kind is not null && target.Source is not null)
                Apply(plan, target.Key, target.Code, target.Kind.Value, target.Source.Value);
        }
    }

    public async Task<ProductRecipeCatalogSummary?> GetCatalogRecipeSummaryAsync(Guid productId,
        CancellationToken token = default)
    {
        var product = await db.Products.AsNoTracking().Where(x => x.Id == productId)
            .Select(x => new { x.Id, x.Sku, x.Description, Unit = x.BaseUnit.Code }).SingleOrDefaultAsync(token);
        if (product is null) return null;
        var recipe = await db.ProductionRecipes.AsNoTracking().Where(x => x.ProductId == productId && x.IsActive)
            .Include(x => x.Lines).ThenInclude(x => x.MaterialProduct).ThenInclude(x => x.BaseUnit)
            .Include(x => x.Lines).ThenInclude(x => x.Stage).SingleOrDefaultAsync(token);
        if (recipe is null)
            return new(product.Id, product.Sku, product.Description, product.Unit, false, false, null, null, null, []);

        var route = await db.ProductionRoutes.AsNoTracking().Where(x => x.ProductId == productId && x.IsActive)
            .Include(x => x.Stages).SingleOrDefaultAsync(token);
        var sequence = route?.Stages.ToDictionary(x => x.StageId, x => x.Sequence) ?? [];
        var catalog = await LoadResolutionCatalogAsync(recipe.Lines.Select(x => x.MaterialProductId),
            recipe.Lines.Where(x => x.StageId.HasValue).Select(x => x.StageId!.Value), token);
        var lines = recipe.Lines.Select(line =>
        {
            var materialInactive = !line.MaterialProduct.IsActive;
            var hasStage = line.StageId.HasValue;
            var stageSequence = hasStage && sequence.TryGetValue(line.StageId!.Value, out var foundSequence)
                ? foundSequence : int.MaxValue;
            var stageInRoute = stageSequence != int.MaxValue && line.Stage?.IsActive == true;
            if (!hasStage)
                return new ProductRecipeCatalogLine(line.MaterialProductId, line.MaterialProduct.Sku,
                    line.MaterialProduct.Description, line.Quantity, line.MaterialProduct.BaseUnit.Code,
                    int.MaxValue, "Pendiente de asignar", "Pendiente hasta asignar etapa", "Sin resolver",
                    materialInactive ? "Material inactivo" : "Requiere etapa",
                    materialInactive ? "El material está inactivo y debe reemplazarse." :
                        "Crea la ruta y asigna la etapa de incorporación.");
            var target = ResolveDefault(catalog, line.MaterialProductId, line.StageId!.Value);
            var status = materialInactive ? "Material inactivo" : !stageInRoute ? "Incompatible" : target.IsAvailable ? "Configurado" :
                target.IsConfigured ? "Incompatible" : "Requiere destino";
            var warning = materialInactive ? "El material está inactivo y requiere atención antes de crear nuevas órdenes." :
                !stageInRoute ? "El proceso de la receta no pertenece a la ruta activa." : target.Warning;
            return new ProductRecipeCatalogLine(line.MaterialProductId, line.MaterialProduct.Sku,
                line.MaterialProduct.Description, line.Quantity, line.MaterialProduct.BaseUnit.Code,
                stageInRoute ? stageSequence : int.MaxValue, line.Stage?.Name ?? "Proceso no disponible",
                target.IsAvailable || target.IsConfigured ? target.Code : "Sin destino automático",
                SourceLabel(target.Source), status, warning);
        }).OrderBy(x => x.Sequence).ThenBy(x => x.MaterialSku, StringComparer.Ordinal).ToArray();
        var complete = route is not null && recipe.Lines.Count > 0 && recipe.Lines.All(x => x.MaterialProduct.IsActive &&
            x.Quantity > 0 && x.StageId.HasValue && sequence.ContainsKey(x.StageId.Value) && x.Stage?.IsActive == true);
        return new(product.Id, product.Sku, product.Description, product.Unit, true, complete, recipe.Version,
            recipe.BaseQuantity, route?.Name, lines);
    }

    public async Task<ProductionPlanningView> GetAsync(Guid orderId, CancellationToken token = default)
    {
        var order = await PlanningOrder(false, orderId, token);
        if (order is null) return new([], ["La orden no existe."], [], false);
        var rows = new List<ProductionPlanningRow>();
        foreach (var plan in order.MaterialPlan.OrderBy(x => x.WorkOrderStage.Sequence).ThenBy(x => x.MaterialProduct.Sku))
            rows.Add(await BuildRowAsync(plan, token));
        var blockers = rows.SelectMany(x => x.Blockers).Distinct().ToList();
        if (order.RecipeVersion is null) blockers.Insert(0, "La orden no tiene una receta fotografiada.");
        if (order.Stages.Count == 0) blockers.Insert(0, "La orden no tiene una ruta fotografiada.");
        if (order.RecipeVersion is not null && rows.Count == 0) blockers.Add("La receta fotografiada no contiene materiales.");
        return new(rows, blockers, rows.SelectMany(x => x.Warnings).Distinct().ToArray(),
            order.Status != ProductionWorkOrderStatus.Draft && (rows.Count == 0 || rows.Any(x => !x.IsResolved)));
    }

    public async Task<IReadOnlyList<string>> ValidateReleaseAsync(ProductionWorkOrder order, CancellationToken token = default)
    {
        var errors = new List<string>();
        if (order.RecipeVersion is null) errors.Add("La orden no tiene una receta fotografiada.");
        if (order.Stages.Count == 0) errors.Add("La orden no tiene una ruta fotografiada.");
        if (order.MaterialPlan.Count == 0) errors.Add("La receta fotografiada no contiene materiales.");
        foreach (var plan in order.MaterialPlan)
        {
            if (!plan.MaterialProduct.IsActive) errors.Add($"{plan.MaterialProduct.Sku}: el material está inactivo.");
            if (plan.PlannedQuantity <= 0) errors.Add($"{plan.MaterialProduct.Sku}: la cantidad planeada no es válida.");
            if (!order.Stages.Any(x => x.Id == plan.WorkOrderStageId)) errors.Add($"{plan.MaterialProduct.Sku}: el proceso no pertenece a la ruta fotografiada.");
            var key = Key(plan);
            if (string.IsNullOrEmpty(key)) errors.Add($"{plan.MaterialProduct.Sku}: el destino WIP no está resuelto.");
            else
            {
                var target = await ValidateTargetAsync(plan.WorkOrderStage.SourceStageId, key, token);
                if (!target.IsAvailable) errors.Add($"{plan.MaterialProduct.Sku}: {target.Warning ?? "el destino WIP no está disponible."}");
            }
        }
        return errors.Distinct().ToArray();
    }

    public async Task<ProductionPlanningResult> ReviewAsync(ReviewProductionPlanningCommand command, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user?.Role.Code != "ADMIN") return new(ProductionPlanningStatus.InvalidPin);
        if (command.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(command.Reason))
            return Invalid("Indica el motivo de la revisión.");
        if (command.Targets.Any(x => !string.IsNullOrWhiteSpace(x.TargetKey) && string.IsNullOrEmpty(NormalizeKey(x.TargetKey))))
            return Invalid("Uno de los destinos WIP enviados no es válido.");
        var normalized = command.Targets.OrderBy(x => x.PlanId)
            .Select(x => new ProductionPlanningTargetInput(x.PlanId, NormalizeKey(x.TargetKey))).ToArray();
        if (normalized.GroupBy(x => x.PlanId).Any(x => x.Count() > 1)) return Invalid("Cada material debe aparecer una sola vez en la revisión.");
        var fingerprint = Hash(JsonSerializer.Serialize(new { command.WorkOrderId, command.ExpectedVersion, Targets = normalized, Reason = command.Reason.Trim() }));
        var prior = await db.ProductionOrderPlanningRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null) return prior.RequestFingerprint == fingerprint ? new(ProductionPlanningStatus.Success) : new(ProductionPlanningStatus.IdempotencyConflict);
        var order = await PlanningOrder(true, command.WorkOrderId, token);
        if (order is null) return new(ProductionPlanningStatus.NotFound);
        if (order.Status != ProductionWorkOrderStatus.Draft) return Invalid("La planificación sólo puede modificarse mientras la orden está en borrador.");
        if (order.Version != command.ExpectedVersion) return new(ProductionPlanningStatus.ConcurrencyConflict);

        var before = Snapshot(order);
        await AddRouteIfMissingAsync(order, token);
        await AddRecipeIfMissingAsync(order, token);
        if (normalized.Any(x => order.MaterialPlan.All(plan => plan.Id != x.PlanId))) return Invalid("La revisión contiene un material que no pertenece a la orden.");
        var inputs = normalized.ToDictionary(x => x.PlanId, x => x.TargetKey);
        var resolutionCatalog = await LoadResolutionCatalogAsync(order.MaterialPlan.Select(x => x.MaterialProductId),
            order.MaterialPlan.Select(x => x.WorkOrderStage.SourceStageId), token);
        var errors = new List<string>();
        foreach (var plan in order.MaterialPlan)
        {
            inputs.TryGetValue(plan.Id, out var requested);
            var current = Key(plan);
            if (!string.IsNullOrEmpty(requested) && requested != current)
            {
                var choice = await ValidateTargetAsync(plan.WorkOrderStage.SourceStageId, requested, token);
                if (!choice.IsAvailable) errors.Add($"{plan.MaterialProduct.Sku}: {choice.Warning ?? "selecciona un destino WIP válido."}");
                else Apply(plan, choice.Key, choice.Label, Kind(choice), ProductionWipResolutionSource.Manual);
            }
            else if (string.IsNullOrEmpty(current))
            {
                var target = ResolveDefault(resolutionCatalog, plan.MaterialProductId, plan.WorkOrderStage.SourceStageId);
                if (target.IsAvailable && target.Kind is not null && target.Source is not null)
                    Apply(plan, target.Key, target.Code, target.Kind.Value, target.Source.Value);
            }
        }
        if (errors.Count > 0) return new(ProductionPlanningStatus.ValidationFailed, errors);
        order.Version++;
        db.ProductionOrderPlanningRevisions.Add(new()
        {
            OperationId = command.OperationId, RequestFingerprint = fingerprint, WorkOrder = order,
            AuthorizedByUserId = user.Id, Reason = command.Reason.Trim(), BeforeJson = before,
            AfterJson = Snapshot(order), RecordedAt = timeProvider.GetUtcNow()
        });
        try { await db.SaveChangesAsync(token); return new(ProductionPlanningStatus.Success); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new(ProductionPlanningStatus.ConcurrencyConflict);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            prior = await db.ProductionOrderPlanningRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
            return prior?.RequestFingerprint == fingerprint ? new(ProductionPlanningStatus.Success) : new(ProductionPlanningStatus.IdempotencyConflict);
        }
    }

    private async Task AddRouteIfMissingAsync(ProductionWorkOrder order, CancellationToken token)
    {
        if (order.Stages.Count > 0) return;
        var route = await db.ProductionRoutes.AsNoTracking().Where(x => x.ProductId == order.ProductId && x.IsActive)
            .Include(x => x.Stages).ThenInclude(x => x.Stage).SingleOrDefaultAsync(token);
        if (route is null || route.Stages.Count == 0 || route.Stages.Any(x => !x.Stage.IsActive)) return;
        foreach (var item in route.Stages.OrderBy(x => x.Sequence))
            order.Stages.Add(new ProductionWorkOrderStage
            {
                SourceStageId = item.StageId, Sequence = item.Sequence, Code = item.Stage.Code, Name = item.Stage.Name
            });
    }

    private async Task AddRecipeIfMissingAsync(ProductionWorkOrder order, CancellationToken token)
    {
        if (order.RecipeVersion is not null || order.MaterialPlan.Count > 0) return;
        var recipe = await db.ProductionRecipes.Include(x => x.Lines).ThenInclude(x => x.MaterialProduct)
            .SingleOrDefaultAsync(x => x.ProductId == order.ProductId && x.IsActive, token);
        if (recipe is null || recipe.Lines.Count == 0 || recipe.Lines.Any(line => !line.MaterialProduct.IsActive ||
                !line.StageId.HasValue || order.Stages.All(stage => stage.SourceStageId != line.StageId.Value))) return;
        foreach (var line in recipe.Lines)
        {
            var stage = order.Stages.Single(x => x.SourceStageId == line.StageId!.Value);
            var planned = decimal.Round(line.Quantity * order.TargetQuantity / recipe.BaseQuantity, 4, MidpointRounding.AwayFromZero);
            var plan = new ProductionOrderMaterialPlan { WorkOrder = order, WorkOrderStage = stage,
                MaterialProduct = line.MaterialProduct, MaterialProductId = line.MaterialProductId,
                UnitId = line.MaterialProduct.BaseUnitId, PlannedQuantity = planned, OriginalPlannedQuantity = planned };
            order.MaterialPlan.Add(plan);
            db.ProductionOrderMaterialPlans.Add(plan);
        }
        order.RecipeVersion = recipe.Version;
        order.UsesBatchTraceability = true;
    }

    private async Task<ProductionPlanningRow> BuildRowAsync(ProductionOrderMaterialPlan plan, CancellationToken token)
    {
        var key = Key(plan);
        var choice = await ValidateTargetAsync(plan.WorkOrderStage.SourceStageId, key, token);
        var blockers = new List<string>();
        if (!plan.MaterialProduct.IsActive) blockers.Add($"{plan.MaterialProduct.Sku}: el material está inactivo.");
        if (plan.PlannedQuantity <= 0) blockers.Add($"{plan.MaterialProduct.Sku}: la cantidad planeada no es válida.");
        if (string.IsNullOrEmpty(key)) blockers.Add($"{plan.MaterialProduct.Sku}: selecciona un destino WIP para {plan.WorkOrderStage.Name}.");
        else if (!choice.IsAvailable) blockers.Add($"{plan.MaterialProduct.Sku}: {choice.Warning ?? "el destino WIP no está disponible."}");
        var (warehouse, wip) = await AvailabilityAsync(plan, token);
        var warnings = new List<string>();
        if (warehouse + wip < plan.PlannedQuantity)
            warnings.Add($"{plan.MaterialProduct.Sku}: faltan {(plan.PlannedQuantity - warehouse - wip):0.####} {plan.Unit.Code}; la existencia no bloquea la liberación.");
        return new(plan.Id, plan.WorkOrderStage.SourceStageId, plan.WorkOrderStage.Sequence, plan.WorkOrderStage.Name,
            plan.MaterialProduct.Sku, plan.MaterialProduct.Description, plan.PlannedQuantity, plan.Unit.Code, key,
            plan.WipTargetCode ?? choice.Label, plan.WipTargetKind?.ToString() ?? "Sin destino", SourceLabel(plan.WipResolutionSource),
            !string.IsNullOrEmpty(key), choice.IsAvailable, warehouse, wip, blockers, warnings);
    }

    private async Task<(decimal Warehouse, decimal Wip)> AvailabilityAsync(ProductionOrderMaterialPlan plan, CancellationToken token)
    {
        var balances = db.InventoryBalances.AsNoTracking().Where(x => x.ProductId == plan.MaterialProductId);
        var warehouse = await balances.Where(x => x.Location.OperationalRole != LocationOperationalRole.Wip &&
            x.Location.IsActive && x.Location.IsPhysicallyPresent && !x.Location.IsBlocked).SumAsync(x => x.Quantity, token);
        var key = Key(plan); var parsed = ProductionWipDefaultService.Parse(key);
        IQueryable<InventoryBalance> target = balances.Where(x => x.Location.OperationalRole == LocationOperationalRole.Wip);
        if (parsed.LocationId is Guid locationId) target = target.Where(x => x.LocationId == locationId);
        else if (parsed.RowCode is not null && parsed.RackNumber is not null)
            target = target.Where(x => x.Location.RowCode == parsed.RowCode && x.Location.RackNumber == parsed.RackNumber);
        else return (warehouse, 0);
        var total = await target.SumAsync(x => x.Quantity, token);
        var destinationIds = await target.Select(x => x.LocationId).Distinct().ToListAsync(token);
        var reserved = await db.ProductionMaterialIssueLinks.AsNoTracking()
            .Where(x => x.ProductId == plan.MaterialProductId && destinationIds.Contains(x.WipLocationId))
            .SumAsync(x => x.Quantity - x.CancelledQuantity - x.OperationLines.Where(line => line.Operation.Type != ProductionMaterialOperationType.Reversal &&
                !db.ProductionMaterialOperations.Any(reverse => reverse.ReversesOperationId == line.Operation.Id)).Sum(line => line.Quantity), token);
        return (warehouse, total - reserved);
    }

    private async Task<ResolutionCatalog> LoadResolutionCatalogAsync(IEnumerable<Guid> materialIds,
        IEnumerable<Guid> stageIds, CancellationToken token)
    {
        var materials = materialIds.Distinct().ToArray();
        var stages = stageIds.Distinct().ToArray();
        var materialDefaults = await db.ProductionMaterialWipDefaults.AsNoTracking()
            .Where(x => materials.Contains(x.ProductId) && stages.Contains(x.ProductionStageId)).ToListAsync(token);
        var stageRows = await db.ProductionStages.AsNoTracking().Where(x => stages.Contains(x.Id)).ToListAsync(token);
        var targets = await db.ProductionProcessWipTargets.AsNoTracking()
            .Where(x => stages.Contains(x.ProductionStageId)).ToListAsync(token);
        var locationIds = materialDefaults.Where(x => x.LocationId.HasValue).Select(x => x.LocationId!.Value)
            .Concat(stageRows.Where(x => x.DefaultWipLocationId.HasValue).Select(x => x.DefaultWipLocationId!.Value))
            .Concat(targets.Where(x => x.LocationId.HasValue).Select(x => x.LocationId!.Value)).Distinct().ToArray();
        var rowCodes = materialDefaults.Select(x => x.RowCode).Concat(stageRows.Select(x => x.DefaultWipRowCode))
            .Concat(targets.Select(x => x.RowCode)).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().ToArray();
        var locations = await db.Locations.AsNoTracking().Where(x => locationIds.Contains(x.Id) ||
            (x.RowCode != null && rowCodes.Contains(x.RowCode))).ToListAsync(token);
        return new(materialDefaults.ToDictionary(x => (x.ProductId, x.ProductionStageId)),
            stageRows.ToDictionary(x => x.Id), targets.GroupBy(x => x.ProductionStageId)
                .ToDictionary(x => x.Key, x => (IReadOnlyList<ProductionProcessWipTarget>)x.ToArray()),
            locations.ToDictionary(x => x.Id), locations);
    }

    private static Resolution ResolveDefault(ResolutionCatalog catalog, Guid materialId, Guid stageId)
    {
        if (!catalog.Stages.TryGetValue(stageId, out var stage))
            return new("", "Proceso inexistente", null, null, false, true, "El proceso ya no existe.");
        if (!stage.IsActive)
            return new("", "Proceso inactivo", null, null, false, true, "El proceso está inactivo.");
        if (catalog.MaterialDefaults.TryGetValue((materialId, stageId), out var material))
        {
            var key = ProductionWipDefaultService.Key(material.LocationId, material.RowCode, material.RackNumber);
            return FromChoice(Describe(catalog, stageId, key), ProductionWipResolutionSource.MaterialDefault, true);
        }
        var processKey = ProductionWipDefaultService.Key(stage.DefaultWipLocationId, stage.DefaultWipRowCode, stage.DefaultWipRackNumber);
        if (!string.IsNullOrEmpty(processKey))
            return FromChoice(Describe(catalog, stageId, processKey), ProductionWipResolutionSource.ProcessDefault, true);
        var choices = catalog.Targets.GetValueOrDefault(stageId, []).Where(x => x.LocationId != null || x.RackNumber != null)
            .Select(x => Describe(catalog, stageId, ProductionWipDefaultService.Key(x.LocationId, x.RowCode, x.RackNumber)))
            .Where(x => x.IsAvailable).DistinctBy(x => x.Key).ToArray();
        return choices.Length == 1
            ? FromChoice(choices[0], ProductionWipResolutionSource.SingleProcessTarget, false)
            : new("", "Sin destino automático", null, null, false, false, choices.Length > 1
                ? "El proceso tiene varios destinos WIP; selecciona uno al planificar la orden."
                : "No existe un destino WIP concreto y elegible para el proceso.");
    }

    private static Resolution FromChoice(WipDefaultChoice choice, ProductionWipResolutionSource source, bool configured) =>
        new(choice.Key, choice.Label, choice.IsAvailable ? Kind(choice) : null, source,
            choice.IsAvailable, configured, choice.Warning);

    private static WipDefaultChoice Describe(ResolutionCatalog catalog, Guid stageId, string? key)
    {
        if (!catalog.Stages.TryGetValue(stageId, out var stage))
            return new(key ?? "", "Proceso inexistente", "Destino WIP", "El proceso ya no existe", false, "El proceso ya no existe.");
        if (string.IsNullOrWhiteSpace(key)) return new("", "Sin destino", "Destino WIP", "Alternativa manual", true);
        var parsed = ProductionWipDefaultService.Parse(key);
        var associations = catalog.Targets.GetValueOrDefault(stageId, []);
        if (parsed.LocationId is Guid locationId)
        {
            if (!catalog.LocationsById.TryGetValue(locationId, out var location))
                return new(key, "Ubicación retirada", "Destino WIP", "La ubicación ya no existe", false, "La ubicación ya no existe.");
            var type = location.Kind == LocationKind.Area ? "Área WIP" : "Posición WIP";
            var associated = location.Kind == LocationKind.Area
                ? associations.Any(x => x.LocationId == location.Id)
                : associations.Any(x => x.RowCode == location.RowCode && (x.RackNumber == null || x.RackNumber == location.RackNumber));
            var valid = stage.IsActive && location.OperationalRole == LocationOperationalRole.Wip && location.IsOperational && associated;
            return new(key, location.Code, type, location.Description ?? type, valid,
                valid ? null : !stage.IsActive ? "El proceso está inactivo." : !location.IsOperational
                    ? "La ubicación está inactiva, bloqueada o retirada." : "El destino ya no es compatible con el proceso.");
        }
        if (parsed.RowCode is not null && parsed.RackNumber is not null)
        {
            var positions = catalog.Locations.Where(x => x.Kind == LocationKind.Rack && x.RowCode == parsed.RowCode &&
                x.RackNumber == parsed.RackNumber).ToArray();
            var associated = associations.Any(x => x.RowCode == parsed.RowCode && (x.RackNumber == null || x.RackNumber == parsed.RackNumber));
            var valid = stage.IsActive && associated && positions.Length > 0 &&
                positions.Any(x => x.OperationalRole == LocationOperationalRole.Wip && x.IsOperational);
            var partial = valid && positions.Any(x => x.OperationalRole == LocationOperationalRole.Wip && !x.IsOperational);
            return new(key, $"{parsed.RowCode}-{parsed.RackNumber}", "Rack WIP", "La posición exacta se confirma al surtir", valid,
                valid ? partial ? "Disponibilidad parcial" : null : "El rack no tiene posiciones WIP disponibles o ya no pertenece al proceso.");
        }
        return new(key ?? "", "Destino inválido", "Destino WIP", "Selecciona un destino válido", false,
            "Selecciona un área, rack o posición WIP.");
    }

    private sealed record Resolution(string Key, string Code, ProductionWipTargetKind? Kind,
        ProductionWipResolutionSource? Source, bool IsAvailable, bool IsConfigured, string? Warning);
    private sealed record ResolutionCatalog(
        IReadOnlyDictionary<(Guid MaterialId, Guid StageId), ProductionMaterialWipDefault> MaterialDefaults,
        IReadOnlyDictionary<Guid, ProductionStage> Stages,
        IReadOnlyDictionary<Guid, IReadOnlyList<ProductionProcessWipTarget>> Targets,
        IReadOnlyDictionary<Guid, Location> LocationsById,
        IReadOnlyList<Location> Locations);

    private Task<WipDefaultChoice> ValidateTargetAsync(Guid stageId, string? key, CancellationToken token) =>
        new ProductionWipDefaultService(db, pins, timeProvider).DescribeAsync(stageId, key, token);
    private Task<ProductionWorkOrder?> PlanningOrder(bool tracked, Guid id, CancellationToken token)
    {
        var query = db.ProductionWorkOrders.Include(x => x.Stages).Include(x => x.MaterialPlan).ThenInclude(x => x.WorkOrderStage)
            .Include(x => x.MaterialPlan).ThenInclude(x => x.MaterialProduct)
            .Include(x => x.MaterialPlan).ThenInclude(x => x.Unit).Include(x => x.PlanningRevisions).AsQueryable();
        return (tracked ? query : query.AsNoTracking()).SingleOrDefaultAsync(x => x.Id == id, token);
    }
    private static void Apply(ProductionOrderMaterialPlan plan, string key, string code, ProductionWipTargetKind kind, ProductionWipResolutionSource source)
    {
        var parsed = ProductionWipDefaultService.Parse(key);
        plan.WipTargetKind = kind; plan.WipLocationId = parsed.LocationId; plan.WipRowCode = parsed.RowCode;
        plan.WipRackNumber = parsed.RackNumber; plan.WipTargetCode = code; plan.WipResolutionSource = source;
    }
    private static string Key(ProductionOrderMaterialPlan plan) => ProductionWipDefaultService.Key(plan.WipLocationId, plan.WipRowCode, plan.WipRackNumber);
    private static ProductionWipTargetKind Kind(WipDefaultChoice choice) => choice.Type.StartsWith("Área", StringComparison.Ordinal) ?
        ProductionWipTargetKind.Area : choice.Type.StartsWith("Rack", StringComparison.Ordinal) ? ProductionWipTargetKind.Rack : ProductionWipTargetKind.Position;
    private static string SourceLabel(ProductionWipResolutionSource? source) => source switch
    {
        ProductionWipResolutionSource.MaterialDefault => "Predeterminado del material",
        ProductionWipResolutionSource.ProcessDefault => "Predeterminado del proceso",
        ProductionWipResolutionSource.SingleProcessTarget => "Único destino del proceso",
        ProductionWipResolutionSource.Manual => "Excepción de la orden",
        _ => "Sin resolver"
    };
    private static string Snapshot(ProductionWorkOrder order) => JsonSerializer.Serialize(new
    {
        order.RecipeVersion,
        Stages = order.Stages.OrderBy(x => x.Sequence)
            .Select(x => new { x.Id, x.SourceStageId, x.Sequence, x.Code, x.Name }),
        Materials = order.MaterialPlan.OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.MaterialProductId, x.WorkOrderStageId, x.PlannedQuantity, Target = Key(x), x.WipTargetCode, x.WipResolutionSource })
    });
    private static string NormalizeKey(string? key)
    {
        var parsed = ProductionWipDefaultService.Parse(key);
        return ProductionWipDefaultService.Key(parsed.LocationId, parsed.RowCode, parsed.RackNumber);
    }
    private static ProductionPlanningResult Invalid(string error) => new(ProductionPlanningStatus.ValidationFailed, [error]);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
