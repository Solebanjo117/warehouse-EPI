using System.Globalization;
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
public sealed record WarehouseMapView(int Version, uint RowVersion, bool IsInitialized, IReadOnlyList<WarehouseMapElementView> Elements, IReadOnlyList<WarehouseMapElementView> Unplaced, int Available, int Blocked, int Inactive, int WithInventory, int Negative, IReadOnlyList<WarehouseMapLayerView> Layers, IReadOnlyList<WarehouseMapArchitecturalElementView> Architecture, IReadOnlyList<WarehouseMapArchitecturalElementView> ArchivedArchitecture, bool UsesLegacyArchitecture, decimal? ScaleUnitsPerInch, string MeasurementSystem);
public sealed record WarehouseMapGeometry(Guid Id, decimal X, decimal Y, decimal Width, decimal Height, short Rotation, int ZIndex, bool IsVisible);
public sealed record WarehouseMapSaveCommand(Guid OperationId, Guid RequestedByUserId, string Pin, string? Reason, IReadOnlyList<WarehouseMapGeometry> Elements, IReadOnlyList<WarehouseMapLayerState> Layers, IReadOnlyList<WarehouseMapArchitectureItem> Architecture, decimal? ScaleUnitsPerInch = null, string MeasurementSystem = "IMPERIAL");
public enum WarehouseMapSaveStatus { Success, InvalidPin, Unauthorized, ValidationFailed, Conflict, IdempotencyConflict, NotInitialized }
public sealed record WarehouseMapSaveResult(WarehouseMapSaveStatus Status, int Version = 0, IReadOnlyList<string>? Errors = null) { public IReadOnlyList<string> ValidationErrors => Errors ?? []; }
public sealed record WarehouseMapRevisionView(Guid Id, int PreviousVersion, int NewVersion, string? Reason, string RequestedBy, string AuthorizedBy, DateTimeOffset RecordedAt, int SchemaVersion, string Summary);
public sealed record WarehouseMapReviewWarning(string Code, string Message, IReadOnlyList<Guid> ElementIds);
public sealed record WarehouseMapReviewSummary(int OperationalModified, int LayerLocksChanged, int Added, int Modified, int Archived, int Restored, bool ScaleChanged, bool MeasurementSystemChanged);
public sealed record WarehouseMapReviewResult(IReadOnlyList<string> Errors, IReadOnlyList<WarehouseMapReviewWarning> Warnings, WarehouseMapReviewSummary Summary);

public sealed class WarehouseMapService(WarehouseDbContext dbContext, UserPinService? pins = null, TimeProvider? timeProvider = null)
{
    public const decimal CanvasWidth = 1600m;
    public const decimal CanvasHeight = 900m;

    public async Task<WarehouseMapView> GetAsync(bool includeProposal, CancellationToken token = default)
    {
        var layout = await dbContext.WarehouseMapLayouts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == 1, token);
        var stored = layout is null ? [] : await dbContext.WarehouseMapElements.AsNoTracking().Where(item => item.LayoutId == 1).OrderBy(item => item.ZIndex).ToListAsync(token);
        var storedLayers = layout is null ? [] : await dbContext.WarehouseMapLayers.AsNoTracking().Where(item => item.LayoutId == 1).OrderBy(item => item.SortOrder).ToListAsync(token);
        var storedArchitecture = layout is null ? [] : await dbContext.WarehouseMapArchitecturalElements.AsNoTracking().Where(item => item.LayoutId == 1).OrderBy(item => item.ZIndex).ToListAsync(token);
        var usesLegacyArchitecture = storedLayers.Count == 0 || storedArchitecture.Count == 0;
        var layers = usesLegacyArchitecture ? WarehouseMapArchitectureCatalog.CreateLayers() : storedLayers;
        var architecture = usesLegacyArchitecture ? WarehouseMapArchitectureCatalog.CreateElements(layers) : storedArchitecture;
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
        var layerCodes = layers.ToDictionary(item => item.Id, item => WarehouseMapArchitectureCatalog.Code(item.Code));
        var architectureViews = architecture.Where(item => !item.IsArchived).OrderBy(item => item.ZIndex).Select(item => WarehouseMapArchitectureCatalog.ToView(item, layerCodes[item.LayerId])).ToArray();
        var archivedArchitectureViews = includeProposal
            ? architecture.Where(item => item.IsArchived).OrderBy(item => item.ZIndex).Select(item => WarehouseMapArchitectureCatalog.ToView(item, layerCodes[item.LayerId])).ToArray()
            : [];
        var layerViews = layers.OrderBy(item => item.SortOrder).Select(item => new WarehouseMapLayerView(item.Id,
            WarehouseMapArchitectureCatalog.Code(item.Code), item.Name, item.SortOrder, item.IsLocked,
            item.Code == WarehouseMapLayerCode.Operations ? elements.Count : architecture.Count(value => value.LayerId == item.Id && !value.IsArchived))).ToArray();
        return new(layout?.Version ?? 0, layout?.RowVersion ?? 0, layout is not null && stored.Count != 0, views.Where(item => item.IsVisible).ToArray(), views.Where(item => !item.IsVisible).ToArray(), locations.Values.Count(item => item.IsActive && !item.IsBlocked), locations.Values.Count(item => item.IsActive && item.IsBlocked), locations.Values.Count(item => !item.IsActive), locations.Values.Count(item => item.HasInventory), locations.Values.Count(item => item.HasNegative), layerViews, architectureViews, archivedArchitectureViews, usesLegacyArchitecture, layout?.ScaleUnitsPerInch, layout?.MeasurementSystem == WarehouseMapMeasurementSystem.Metric ? "METRIC" : "IMPERIAL");
    }

    public async Task<IReadOnlyList<WarehouseMapRevisionView>> GetRevisionsAsync(int take = 20, CancellationToken token = default)
    {
        var rows = await dbContext.WarehouseMapRevisions.AsNoTracking().OrderByDescending(item => item.RecordedAt)
            .Take(Math.Clamp(take, 1, 100)).Select(item => new
            {
                item.Id,
                item.PreviousVersion,
                item.NewVersion,
                item.Reason,
                RequestedBy = item.RequestedByUser.FullName,
                AuthorizedBy = item.AuthorizedByUser.FullName,
                item.RecordedAt,
                item.ChangesJson
            }).ToListAsync(token);
        return rows.Select(item =>
        {
            var (schema, summary) = SummarizeRevision(item.ChangesJson);
            return new WarehouseMapRevisionView(item.Id, item.PreviousVersion, item.NewVersion, item.Reason,
                item.RequestedBy, item.AuthorizedBy, item.RecordedAt, schema, summary);
        }).ToArray();
    }

    public async Task<WarehouseMapReviewResult> ReviewAsync(WarehouseMapSaveCommand command, CancellationToken token = default)
    {
        var errors = Validate(command.OperationId, command.RequestedByUserId, command.Reason, command.Elements,
            command.Layers, command.Architecture, command.ScaleUnitsPerInch, command.MeasurementSystem);
        var requester = await dbContext.Users.AsNoTracking().Include(item => item.Role)
            .SingleOrDefaultAsync(item => item.Id == command.RequestedByUserId, token);
        if (requester is null || !requester.IsActive || requester.Role.Code != "ADMIN")
            errors.Add("La sesión ADMIN ya no es válida.");
        var layout = await dbContext.WarehouseMapLayouts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == 1, token);
        if (layout is null) errors.Add("Primero confirma la distribución inicial.");
        var layers = await dbContext.WarehouseMapLayers.AsNoTracking().Where(item => item.LayoutId == 1)
            .OrderBy(item => item.SortOrder).ToListAsync(token);
        var architecture = await dbContext.WarehouseMapArchitecturalElements.AsNoTracking().Where(item => item.LayoutId == 1)
            .OrderBy(item => item.ZIndex).ToListAsync(token);
        if (layout is not null)
        {
            var submittedIds = command.Architecture.Select(item => item.Id).ToHashSet();
            if (architecture.Any(item => !submittedIds.Contains(item.Id)))
                errors.Add("Todos los elementos arquitectónicos guardados, incluso los archivados, deben conservarse.");
            if (layers.Count != 0)
            {
                var layerByCode = layers.ToDictionary(item => WarehouseMapArchitectureCatalog.Code(item.Code));
                errors.AddRange(ValidateArchitectureDefinitions(architecture, layers));
                errors.AddRange(ValidateArchitectureSubmission(command.Architecture,
                    architecture.ToDictionary(item => item.Id), layerByCode, command.Layers));
            }
        }
        errors = errors.Distinct(StringComparer.Ordinal).ToList();
        if (errors.Count != 0)
            return new(errors, [], new(0, 0, 0, 0, 0, 0, false, false));

        var currentById = architecture.ToDictionary(item => item.Id);
        var layerCodes = layers.ToDictionary(item => item.Id, item => WarehouseMapArchitectureCatalog.Code(item.Code));
        var before = architecture.Select(item => WarehouseMapArchitectureCatalog.ToItem(item, layerCodes[item.LayerId]))
            .ToDictionary(item => item.Id);
        var added = command.Architecture.Count(item => !currentById.ContainsKey(item.Id));
        var modified = command.Architecture.Count(item => before.TryGetValue(item.Id, out var old) && !SameFullValues(item, old));
        var archived = command.Architecture.Count(item => before.TryGetValue(item.Id, out var old) && !old.IsArchived && item.IsArchived);
        var restored = command.Architecture.Count(item => before.TryGetValue(item.Id, out var old) && old.IsArchived && !item.IsArchived);
        var storedOperational = await dbContext.WarehouseMapElements.AsNoTracking().Where(item => item.LayoutId == 1)
            .Select(item => new WarehouseMapGeometry(item.Id, item.X, item.Y, item.Width, item.Height, item.Rotation, item.ZIndex, item.IsVisible))
            .ToDictionaryAsync(item => item.Id, token);
        var operationalModified = command.Elements.Count(item => !storedOperational.TryGetValue(item.Id, out var old) || old != item);
        var currentLayerStates = layers.ToDictionary(item => WarehouseMapArchitectureCatalog.Code(item.Code), item => item.IsLocked);
        var layerLocksChanged = command.Layers.Count(item => !currentLayerStates.TryGetValue(item.Code, out var old) || old != item.IsLocked);
        var warnings = await BuildWarningsAsync(command.Elements, command.Architecture,
            command.ScaleUnitsPerInch, command.MeasurementSystem, token);
        return new([], warnings, new(operationalModified, layerLocksChanged, added, modified, archived, restored,
            layout!.ScaleUnitsPerInch != command.ScaleUnitsPerInch,
            (layout.MeasurementSystem == WarehouseMapMeasurementSystem.Metric ? "METRIC" : "IMPERIAL") != command.MeasurementSystem));
    }

    public async Task<WarehouseMapSaveResult> InitializeAsync(Guid operationId, Guid requestedByUserId, string pin, string? reason, IReadOnlyList<WarehouseMapGeometry>? geometry = null, IReadOnlyList<WarehouseMapLayerState>? layers = null, IReadOnlyList<WarehouseMapArchitectureItem>? architecture = null, decimal? scaleUnitsPerInch = null, string measurementSystem = "IMPERIAL", CancellationToken token = default)
    {
        var proposal = await BuildProposalAsync(token);
        var geometries = geometry?.ToArray() ?? proposal.Select(ToGeometry).ToArray();
        var byId = proposal.ToDictionary(item => item.Id);
        if (geometries.Length == byId.Count && geometries.All(item => byId.ContainsKey(item.Id))) foreach (var item in geometries) { var target = byId[item.Id]; target.X = item.X; target.Y = item.Y; target.Width = item.Width; target.Height = item.Height; target.Rotation = item.Rotation; target.ZIndex = item.ZIndex; target.IsVisible = item.IsVisible; }
        var initialLayers = WarehouseMapArchitectureCatalog.CreateLayers();
        var initialArchitecture = WarehouseMapArchitectureCatalog.CreateElements(initialLayers);
        var layerStates = layers?.ToArray() ?? initialLayers.Select(ToLayerState).ToArray();
        var initialLayerCodes = initialLayers.ToDictionary(item => item.Id, item => WarehouseMapArchitectureCatalog.Code(item.Code));
        var architectureItems = architecture?.ToArray() ?? initialArchitecture
            .Select(item => WarehouseMapArchitectureCatalog.ToItem(item, initialLayerCodes[item.LayerId])).ToArray();
        return await SaveCoreAsync(operationId, requestedByUserId, pin, reason, geometries, layerStates,
            architectureItems, scaleUnitsPerInch, measurementSystem, true, token, proposal, initialLayers, initialArchitecture);
    }

    public Task<WarehouseMapSaveResult> SaveAsync(WarehouseMapSaveCommand command, CancellationToken token = default) =>
        SaveCoreAsync(command.OperationId, command.RequestedByUserId, command.Pin, command.Reason, command.Elements,
            command.Layers, command.Architecture, command.ScaleUnitsPerInch, command.MeasurementSystem, false, token);

    private async Task<WarehouseMapSaveResult> SaveCoreAsync(Guid operationId, Guid requestedByUserId, string pin,
        string? reason, IReadOnlyList<WarehouseMapGeometry> geometries, IReadOnlyList<WarehouseMapLayerState> layerStates,
        IReadOnlyList<WarehouseMapArchitectureItem> architectureItems, decimal? scaleUnitsPerInch,
        string measurementSystem, bool initialize, CancellationToken token,
        List<WarehouseMapElement>? initialElements = null, List<WarehouseMapLayer>? initialLayers = null,
        List<WarehouseMapArchitecturalElement>? initialArchitecture = null)
    {
        var errors = Validate(operationId, requestedByUserId, reason, geometries, layerStates, architectureItems,
            scaleUnitsPerInch, measurementSystem);
        if (errors.Count != 0) return new(WarehouseMapSaveStatus.ValidationFailed, Errors: errors);
        var requester = await dbContext.Users.AsNoTracking().Include(item => item.Role).SingleOrDefaultAsync(item => item.Id == requestedByUserId, token);
        if (requester is null || !requester.IsActive || requester.Role.Code != "ADMIN") return new(WarehouseMapSaveStatus.Unauthorized);
        if (pins is null) return new(WarehouseMapSaveStatus.Unauthorized);
        var authorized = await pins.AuthenticateAsync(pin, token);
        if (authorized is null || authorized.Role.Code != "ADMIN") return new(WarehouseMapSaveStatus.InvalidPin);
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var payload = JsonSerializer.Serialize(new
        {
            Elements = geometries.OrderBy(item => item.Id),
            Layers = layerStates.OrderBy(item => item.Code),
            Architecture = architectureItems.OrderBy(item => item.Id),
            ScaleUnitsPerInch = scaleUnitsPerInch,
            MeasurementSystem = measurementSystem
        });
        var fingerprint = Hash($"{requestedByUserId:N}|{authorized.Id:N}|{normalizedReason}|{payload}");
        var existingRevision = await dbContext.WarehouseMapRevisions.AsNoTracking().SingleOrDefaultAsync(item => item.OperationId == operationId, token);
        if (existingRevision is not null) return new(existingRevision.RequestFingerprint == fingerprint ? WarehouseMapSaveStatus.Success : WarehouseMapSaveStatus.IdempotencyConflict, existingRevision.NewVersion);
        await using var transaction = dbContext.Database.IsRelational() ? await dbContext.Database.BeginTransactionAsync(token) : null;
        var useDirectUpdates = !initialize && dbContext.Database.IsRelational();
        if (useDirectUpdates && dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            await dbContext.Database.ExecuteSqlRawAsync("SELECT id FROM warehouse_map_layouts WHERE id = 1 FOR UPDATE", token);
        var layout = !useDirectUpdates
            ? await dbContext.WarehouseMapLayouts.Include(item => item.Elements).Include(item => item.Layers)
                .Include(item => item.ArchitecturalElements).SingleOrDefaultAsync(item => item.Id == 1, token)
            : await dbContext.WarehouseMapLayouts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == 1, token);
        List<WarehouseMapLayer> mapLayers;
        List<WarehouseMapArchitecturalElement> architecturalElements;
        var usesLegacyArchitecture = false;
        if (initialize)
        {
            if (layout is not null) return new(WarehouseMapSaveStatus.Conflict, layout.Version);
            layout = new WarehouseMapLayout
            {
                Id = 1,
                Version = 0,
                ScaleUnitsPerInch = scaleUnitsPerInch,
                MeasurementSystem = ParseMeasurementSystem(measurementSystem)
            };
            dbContext.WarehouseMapLayouts.Add(layout);
            var proposal = initialElements ?? await BuildProposalAsync(token);
            foreach (var item in proposal) layout.Elements.Add(item);
            mapLayers = initialLayers ?? WarehouseMapArchitectureCatalog.CreateLayers();
            architecturalElements = initialArchitecture ?? WarehouseMapArchitectureCatalog.CreateElements(mapLayers);
            foreach (var item in mapLayers) layout.Layers.Add(item);
            foreach (var item in architecturalElements) layout.ArchitecturalElements.Add(item);
        }
        else if (layout is null) return new(WarehouseMapSaveStatus.NotInitialized);
        else
        {
            if (useDirectUpdates)
            {
                layout.Elements = await dbContext.WarehouseMapElements.AsNoTracking().Where(item => item.LayoutId == 1).ToListAsync(token);
                mapLayers = await dbContext.WarehouseMapLayers.AsNoTracking().Where(item => item.LayoutId == 1).OrderBy(item => item.SortOrder).ToListAsync(token);
                architecturalElements = await dbContext.WarehouseMapArchitecturalElements.AsNoTracking().Where(item => item.LayoutId == 1).OrderBy(item => item.ZIndex).ToListAsync(token);
            }
            else
            {
                mapLayers = layout.Layers.OrderBy(item => item.SortOrder).ToList();
                architecturalElements = layout.ArchitecturalElements.OrderBy(item => item.ZIndex).ToList();
            }

            if (mapLayers.Count == 0 && architecturalElements.Count == 0)
            {
                usesLegacyArchitecture = true;
                mapLayers = WarehouseMapArchitectureCatalog.CreateLayers();
                architecturalElements = WarehouseMapArchitectureCatalog.CreateElements(mapLayers);
                if (!useDirectUpdates)
                {
                    foreach (var item in mapLayers) layout.Layers.Add(item);
                    foreach (var item in architecturalElements) layout.ArchitecturalElements.Add(item);
                    dbContext.WarehouseMapLayers.AddRange(mapLayers);
                    dbContext.WarehouseMapArchitecturalElements.AddRange(architecturalElements);
                }
            }
            else if (mapLayers.Count == 0 || architecturalElements.Count == 0)
            {
                return new(WarehouseMapSaveStatus.ValidationFailed, layout.Version,
                    ["La capa arquitectónica está incompleta. No se modificó el croquis."]);
            }
        }

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
        var layerByCode = mapLayers.ToDictionary(item => WarehouseMapArchitectureCatalog.Code(item.Code));
        var submittedLayerCodes = layerStates.Select(item => item.Code).ToHashSet(StringComparer.Ordinal);
        if (layerByCode.Keys.Except(submittedLayerCodes, StringComparer.Ordinal).Any()
            || submittedLayerCodes.Except(layerByCode.Keys, StringComparer.Ordinal).Any())
            return new(WarehouseMapSaveStatus.ValidationFailed, layout.Version,
                ["La lista de capas ya no coincide con el croquis actual."]);

        var architectureById = architecturalElements.ToDictionary(item => item.Id);
        var submittedArchitectureIds = architectureItems.Select(item => item.Id).ToHashSet();
        if (architectureById.Keys.Except(submittedArchitectureIds).Any())
            return new(WarehouseMapSaveStatus.ValidationFailed, layout.Version,
                ["No se pueden eliminar elementos arquitectónicos que ya fueron guardados."]);

        var architectureDefinitionErrors = ValidateArchitectureDefinitions(architecturalElements, mapLayers);
        if (architectureDefinitionErrors.Count != 0)
            return new(WarehouseMapSaveStatus.ValidationFailed, layout.Version, architectureDefinitionErrors);
        var architectureSubmissionErrors = ValidateArchitectureSubmission(
            architectureItems, architectureById, layerByCode, layerStates);
        if (architectureSubmissionErrors.Count != 0)
            return new(WarehouseMapSaveStatus.ValidationFailed, layout.Version, architectureSubmissionErrors);

        var before = layout.Elements.OrderBy(item => item.Id).Select(ToGeometry).ToArray();
        var beforeLayers = mapLayers.OrderBy(item => item.SortOrder).Select(ToLayerState).ToArray();
        var layerCodesById = mapLayers.ToDictionary(item => item.Id, item => WarehouseMapArchitectureCatalog.Code(item.Code));
        var beforeArchitecture = architecturalElements.OrderBy(item => item.Id)
            .Select(item => WarehouseMapArchitectureCatalog.ToItem(item, layerCodesById[item.LayerId])).ToArray();
        var beforeScale = layout.ScaleUnitsPerInch;
        var beforeMeasurementSystem = layout.MeasurementSystem == WarehouseMapMeasurementSystem.Metric ? "METRIC" : "IMPERIAL";
        foreach (var geometry in geometries)
        {
            var item = byId[geometry.Id]; item.X = geometry.X; item.Y = geometry.Y; item.Width = geometry.Width; item.Height = geometry.Height; item.Rotation = geometry.Rotation; item.ZIndex = geometry.ZIndex; item.IsVisible = geometry.IsVisible;
        }
        foreach (var state in layerStates) layerByCode[state.Code].IsLocked = state.IsLocked;
        var architecturalAdditionIds = architectureItems.Where(item => !architectureById.ContainsKey(item.Id))
            .Select(item => item.Id).ToHashSet();
        foreach (var submitted in architectureItems)
        {
            if (!architectureById.TryGetValue(submitted.Id, out var item))
            {
                item = WarehouseMapArchitectureCatalog.CreateElement(
                    submitted, layerByCode[submitted.LayerCode], submitted.ZIndex);
                architecturalElements.Add(item);
                architectureById.Add(item.Id, item);
                if (!useDirectUpdates) dbContext.WarehouseMapArchitecturalElements.Add(item);
            }
            else
            {
                item.Label = string.IsNullOrWhiteSpace(submitted.Label) ? null : submitted.Label.Trim();
                item.GeometryJson = WarehouseMapArchitectureCatalog.WriteGeometry(submitted);
                item.StrokeToken = submitted.StrokeToken;
                item.FillToken = submitted.FillToken;
                item.StrokeWidth = submitted.StrokeWidth;
                item.IsDashed = submitted.IsDashed;
                item.ZIndex = submitted.ZIndex;
                item.IsLocked = submitted.IsLocked;
                item.GroupId = submitted.GroupId;
                item.IsArchived = submitted.IsArchived;
            }
        }
        var afterArchitecture = architecturalElements.OrderBy(item => item.Id)
            .Select(item => WarehouseMapArchitectureCatalog.ToItem(item, layerCodesById[item.LayerId])).ToArray();
        var previousVersion = layout.Version;
        var newVersion = previousVersion + 1;
        var recordedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var changes = JsonSerializer.Serialize(new
        {
            SchemaVersion = 4,
            Operational = new { Before = before, After = geometries.OrderBy(item => item.Id) },
            Layers = new { Before = beforeLayers, After = layerStates.OrderBy(item => item.Code) },
            Scale = new
            {
                BeforeUnitsPerInch = beforeScale,
                AfterUnitsPerInch = scaleUnitsPerInch,
                BeforeMeasurementSystem = beforeMeasurementSystem,
                AfterMeasurementSystem = measurementSystem
            },
            Architecture = new
            {
                Before = beforeArchitecture,
                After = afterArchitecture,
                Added = architecturalAdditionIds.OrderBy(item => item),
                Modified = architectureItems.Where(item => beforeArchitecture.Any(old => old.Id == item.Id && !SameFullValues(old, item))).Select(item => item.Id).OrderBy(item => item),
                Archived = architectureItems.Where(item => beforeArchitecture.Any(old => old.Id == item.Id && !old.IsArchived && item.IsArchived)).Select(item => item.Id).OrderBy(item => item),
                Restored = architectureItems.Where(item => beforeArchitecture.Any(old => old.Id == item.Id && old.IsArchived && !item.IsArchived)).Select(item => item.Id).OrderBy(item => item)
            }
        });
        if (!useDirectUpdates)
        {
            layout.Version = newVersion;
            layout.UpdatedAt = recordedAt;
            layout.UpdatedByUserId = authorized.Id;
            layout.ScaleUnitsPerInch = scaleUnitsPerInch;
            layout.MeasurementSystem = ParseMeasurementSystem(measurementSystem);
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
            if (usesLegacyArchitecture)
            {
                dbContext.WarehouseMapLayers.AddRange(mapLayers);
                dbContext.WarehouseMapArchitecturalElements.AddRange(architecturalElements);
            }
            else
            {
                foreach (var state in layerStates)
                {
                    var affected = await dbContext.WarehouseMapLayers.Where(item => item.Id == layerByCode[state.Code].Id)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsLocked, state.IsLocked), token);
                    if (affected != 1) return new(WarehouseMapSaveStatus.ValidationFailed, previousVersion,
                        ["Una de las capas del croquis ya no existe."]);
                }
                foreach (var submitted in architectureItems.Where(item => !architecturalAdditionIds.Contains(item.Id)))
                {
                    var geometryJson = WarehouseMapArchitectureCatalog.WriteGeometry(submitted);
                    var label = string.IsNullOrWhiteSpace(submitted.Label) ? null : submitted.Label.Trim();
                    var affected = await dbContext.WarehouseMapArchitecturalElements.Where(item => item.Id == submitted.Id)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(item => item.Label, label)
                            .SetProperty(item => item.GeometryJson, geometryJson)
                            .SetProperty(item => item.StrokeToken, submitted.StrokeToken)
                            .SetProperty(item => item.FillToken, submitted.FillToken)
                            .SetProperty(item => item.StrokeWidth, submitted.StrokeWidth)
                            .SetProperty(item => item.IsDashed, submitted.IsDashed)
                            .SetProperty(item => item.ZIndex, submitted.ZIndex)
                            .SetProperty(item => item.IsLocked, submitted.IsLocked)
                            .SetProperty(item => item.GroupId, submitted.GroupId)
                            .SetProperty(item => item.IsArchived, submitted.IsArchived), token);
                    if (affected != 1) return new(WarehouseMapSaveStatus.ValidationFailed, previousVersion,
                        ["Uno de los elementos arquitectónicos ya no existe."]);
                }
                if (architecturalAdditionIds.Count != 0)
                    dbContext.WarehouseMapArchitecturalElements.AddRange(
                        architecturalAdditionIds.Select(id => architectureById[id]));
            }
            var layoutAffected = await dbContext.WarehouseMapLayouts.Where(item => item.Id == 1).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Version, newVersion)
                .SetProperty(item => item.UpdatedAt, recordedAt)
                .SetProperty(item => item.UpdatedByUserId, authorized.Id)
                .SetProperty(item => item.ScaleUnitsPerInch, scaleUnitsPerInch)
                .SetProperty(item => item.MeasurementSystem, ParseMeasurementSystem(measurementSystem)), token);
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

    private static List<string> Validate(Guid operationId, Guid userId, string? reason,
        IReadOnlyList<WarehouseMapGeometry> elements, IReadOnlyList<WarehouseMapLayerState> layers,
        IReadOnlyList<WarehouseMapArchitectureItem> architecture, decimal? scaleUnitsPerInch,
        string measurementSystem)
    {
        var errors = new List<string>();
        if (operationId == Guid.Empty || userId == Guid.Empty) errors.Add("La operación y el solicitante son obligatorios.");
        if ((reason?.Trim().Length ?? 0) > 500) errors.Add("El motivo admite hasta 500 caracteres.");
        if (elements.Count is 0 or > 1000 || elements.Select(item => item.Id).Distinct().Count() != elements.Count)
            errors.Add("La colección de elementos no es válida.");
        if (elements.Any(item => item.X < 0 || item.Y < 0 || item.Width < 10 || item.Height < 10
            || item.X + item.Width > CanvasWidth || item.Y + item.Height > CanvasHeight
            || item.Rotation is not (0 or 90 or 180 or 270)))
            errors.Add("La geometría debe permanecer dentro del croquis y usar rotaciones de 90°.");

        var validLayerCodes = Enum.GetValues<WarehouseMapLayerCode>().Select(WarehouseMapArchitectureCatalog.Code).ToHashSet();
        if (layers.Count != validLayerCodes.Count || layers.Select(item => item.Code).Distinct(StringComparer.Ordinal).Count() != layers.Count
            || layers.Any(item => !validLayerCodes.Contains(item.Code)))
            errors.Add("La colección de capas no es válida.");
        if (architecture.Count is 0 or > 500 || architecture.Select(item => item.Id).Distinct().Count() != architecture.Count)
            errors.Add("La colección arquitectónica no es válida.");
        if (architecture.Any(item => !ValidArchitectureGeometry(item)))
            errors.Add("La geometría arquitectónica debe permanecer dentro del croquis y conservar su forma.");
        if (scaleUnitsPerInch is <= 0 or > 100000)
            errors.Add("La escala debe ser positiva y pertenecer al rango permitido.");
        if (measurementSystem is not ("IMPERIAL" or "METRIC"))
            errors.Add("El sistema de medición debe ser IMPERIAL o METRIC.");
        foreach (var group in architecture.Where(item => item.GroupId.HasValue).GroupBy(item => item.GroupId!.Value))
        {
            if (group.Count() < 2 || group.Select(item => item.LayerCode).Distinct(StringComparer.Ordinal).Count() != 1)
                errors.Add("Los grupos deben contener al menos dos elementos de una misma capa.");
            if (group.Select(item => item.IsArchived).Distinct().Count() != 1)
                errors.Add("Todos los integrantes de un grupo deben compartir el estado archivado.");
        }
        var dimensions = architecture.Where(item => item.LayerCode == "DIMENSIONS").ToArray();
        if (dimensions.Length != 0 && scaleUnitsPerInch is null)
            errors.Add("Calibra la escala antes de crear cotas.");
        foreach (var group in dimensions.GroupBy(item => item.GroupId))
        {
            if (group.Key is null || group.Count() != 2 || group.Count(item => item.Kind.Equals("Polyline", StringComparison.OrdinalIgnoreCase)) != 1
                || group.Count(item => item.Kind.Equals("Text", StringComparison.OrdinalIgnoreCase)) != 1)
                errors.Add("Cada cota debe contener exactamente una línea y un texto agrupados.");
            else if (scaleUnitsPerInch is decimal scale)
            {
                var line = group.Single(item => item.Kind.Equals("Polyline", StringComparison.OrdinalIgnoreCase));
                var text = group.Single(item => item.Kind.Equals("Text", StringComparison.OrdinalIgnoreCase));
                var start = line.Points[0];
                var end = line.Points[^1];
                var length = (decimal)Math.Sqrt((double)((end.X - start.X) * (end.X - start.X) + (end.Y - start.Y) * (end.Y - start.Y)));
                if (!string.Equals(text.Label?.Trim(), FormatDistance(length / scale, measurementSystem), StringComparison.Ordinal))
                    errors.Add("El texto de una cota debe corresponder con su distancia calibrada.");
            }
        }
        return errors;
    }

    private static bool ValidArchitectureGeometry(WarehouseMapArchitectureItem item)
    {
        if (item.Id == Guid.Empty || item.Width < 1 || item.Height < 1
            || item.Rotation is not (0 or 90 or 180 or 270) || item.CornerRadius < 0
            || item.CornerRadius > Math.Min(item.Width, item.Height) / 2 || item.Points.Count > 64)
            return false;
        if (item.Points.Any(point => point.X < 0 || point.Y < 0 || point.X > item.Width || point.Y > item.Height))
            return false;
        var localPoints = item.Kind.Equals(nameof(WarehouseMapArchitecturalElementKind.Polyline), StringComparison.OrdinalIgnoreCase)
            ? item.Points
            : [new WarehouseMapPoint(0, 0), new(item.Width, 0), new(item.Width, item.Height), new(0, item.Height)];
        return localPoints.All(point =>
        {
            var transformed = item.Rotation switch
            {
                90 => new WarehouseMapPoint(-point.Y, point.X),
                180 => new WarehouseMapPoint(-point.X, -point.Y),
                270 => new WarehouseMapPoint(point.Y, -point.X),
                _ => point
            };
            var x = item.X + transformed.X;
            var y = item.Y + transformed.Y;
            return x >= 0 && y >= 0 && x <= CanvasWidth && y <= CanvasHeight;
        });
    }

    private static List<string> ValidateArchitectureDefinitions(
        IReadOnlyList<WarehouseMapArchitecturalElement> elements, IReadOnlyList<WarehouseMapLayer> layers)
    {
        var errors = new List<string>();
        var layerIds = layers.Select(item => item.Id).ToHashSet();
        if (elements.Any(item => !layerIds.Contains(item.LayerId)))
            errors.Add("Un elemento arquitectónico no pertenece a una capa válida.");
        if (elements.Any(item => item.Label?.Length > 120))
            errors.Add("Los textos arquitectónicos admiten hasta 120 caracteres.");
        if (elements.Any(item => !WarehouseMapArchitectureCatalog.StyleTokens.Contains(item.StrokeToken)
            || !WarehouseMapArchitectureCatalog.StyleTokens.Contains(item.FillToken)
            || item.StrokeWidth is < 0 or > 12))
            errors.Add("El estilo arquitectónico no pertenece al catálogo permitido.");
        foreach (var item in elements)
        {
            WarehouseMapStoredGeometry geometry;
            try { geometry = WarehouseMapArchitectureCatalog.ReadGeometry(item); }
            catch (JsonException) { errors.Add("Un elemento arquitectónico contiene geometría JSON inválida."); continue; }
            catch (InvalidOperationException) { errors.Add("Un elemento arquitectónico contiene geometría JSON inválida."); continue; }
            var layerCode = WarehouseMapArchitectureCatalog.Code(layers.Single(layer => layer.Id == item.LayerId).Code);
            var posted = new WarehouseMapArchitectureItem(item.Id, layerCode, item.Kind.ToString(), item.Label,
                geometry.X, geometry.Y, geometry.Width, geometry.Height, geometry.Rotation,
                geometry.CornerRadius, geometry.Points, item.StrokeToken, item.FillToken, item.StrokeWidth,
                item.IsDashed, item.ZIndex, item.IsLocked, item.GroupId, item.IsArchived);
            if (!ValidArchitectureGeometry(posted)) errors.Add("Un elemento arquitectónico contiene geometría inválida.");
            if (item.Kind == WarehouseMapArchitecturalElementKind.Polyline && geometry.Points.Count is < 2 or > 64)
                errors.Add("Una polilínea arquitectónica debe contener entre 2 y 64 puntos.");
            if (item.Kind != WarehouseMapArchitecturalElementKind.Polyline && geometry.Points.Count != 0)
                errors.Add("Solo las polilíneas pueden contener puntos.");
            if (item.Kind == WarehouseMapArchitecturalElementKind.Text && string.IsNullOrWhiteSpace(item.Label))
                errors.Add("Un texto arquitectónico no puede estar vacío.");
        }
        return errors.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<string> ValidateArchitectureSubmission(
        IReadOnlyList<WarehouseMapArchitectureItem> submitted,
        IReadOnlyDictionary<Guid, WarehouseMapArchitecturalElement> current,
        IReadOnlyDictionary<string, WarehouseMapLayer> layers,
        IReadOnlyList<WarehouseMapLayerState> layerStates)
    {
        var errors = new List<string>();
        var stateByCode = layerStates.ToDictionary(item => item.Code, StringComparer.Ordinal);
        foreach (var item in submitted)
        {
            if (!layers.TryGetValue(item.LayerCode, out var layer) || item.LayerCode == "OPERATIONS")
            {
                errors.Add("Un elemento arquitectónico no pertenece a una capa editable válida.");
                continue;
            }
            if (!Enum.TryParse<WarehouseMapArchitecturalElementKind>(item.Kind, true, out var kind)
                || !Compatible(item.LayerCode, kind))
                errors.Add("El tipo arquitectónico no es compatible con su capa.");
            if (item.Label?.Trim().Length > 120
                || kind == WarehouseMapArchitecturalElementKind.Text && string.IsNullOrWhiteSpace(item.Label))
                errors.Add("Los textos arquitectónicos son obligatorios y admiten hasta 120 caracteres.");
            if (!WarehouseMapArchitectureCatalog.StyleTokens.Contains(item.StrokeToken)
                || !WarehouseMapArchitectureCatalog.StyleTokens.Contains(item.FillToken)
                || item.StrokeWidth is < 0 or > 12)
                errors.Add("El estilo arquitectónico no pertenece al catálogo permitido.");
            if (kind == WarehouseMapArchitecturalElementKind.Polyline && item.Points.Count is < 2 or > 64)
                errors.Add("Una polilínea arquitectónica debe contener entre 2 y 64 puntos.");
            if (kind != WarehouseMapArchitecturalElementKind.Polyline && item.Points.Count != 0)
                errors.Add("Solo las polilíneas pueden contener puntos.");
            if (kind != WarehouseMapArchitecturalElementKind.Rectangle && item.CornerRadius != 0)
                errors.Add("Solo los rectángulos pueden tener esquinas redondeadas.");

            if (current.TryGetValue(item.Id, out var existing))
            {
                var currentLayerCode = layers.Single(value => value.Value.Id == existing.LayerId).Key;
                var currentItem = WarehouseMapArchitectureCatalog.ToItem(existing, currentLayerCode);
                if (!item.LayerCode.Equals(currentLayerCode, StringComparison.Ordinal)
                    || !item.Kind.Equals(existing.Kind.ToString(), StringComparison.OrdinalIgnoreCase))
                    errors.Add("La capa y el tipo de un elemento guardado no pueden cambiar.");
                if (stateByCode[item.LayerCode].IsLocked && !SameFullValues(item, currentItem)
                    || item.IsLocked && !SameValuesIgnoringLock(item, currentItem))
                    errors.Add("Desbloquea la capa antes de modificar sus elementos.");
                if (existing.IsArchived && item.IsArchived && !SameEditableValues(item, currentItem))
                    errors.Add("Restaura el elemento antes de modificar su geometría o estilo.");
            }
            else
            {
                if (item.IsArchived || item.IsLocked || stateByCode[item.LayerCode].IsLocked)
                    errors.Add("Desbloquea la capa antes de dibujar un elemento.");
            }
        }
        return errors.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool Compatible(string layerCode, WarehouseMapArchitecturalElementKind kind) => layerCode switch
    {
        "STRUCTURE" or "AISLES" or "ZONES" => kind is WarehouseMapArchitecturalElementKind.Rectangle
            or WarehouseMapArchitecturalElementKind.Polyline,
        "TEXT" => kind == WarehouseMapArchitecturalElementKind.Text,
        "DIMENSIONS" => kind is WarehouseMapArchitecturalElementKind.Polyline or WarehouseMapArchitecturalElementKind.Text,
        _ => false
    };

    private static bool SameEditableValues(WarehouseMapArchitectureItem first, WarehouseMapArchitectureItem second) =>
        first.Label?.Trim() == second.Label?.Trim() && first.X == second.X && first.Y == second.Y
        && first.Width == second.Width && first.Height == second.Height && first.Rotation == second.Rotation
        && first.CornerRadius == second.CornerRadius && first.Points.SequenceEqual(second.Points)
        && first.StrokeToken == second.StrokeToken && first.FillToken == second.FillToken
        && first.StrokeWidth == second.StrokeWidth && first.IsDashed == second.IsDashed;

    private static bool SameFullValues(WarehouseMapArchitectureItem first, WarehouseMapArchitectureItem second) =>
        SameEditableValues(first, second) && first.ZIndex == second.ZIndex && first.IsLocked == second.IsLocked
        && first.GroupId == second.GroupId && first.IsArchived == second.IsArchived;

    private static bool SameValuesIgnoringLock(WarehouseMapArchitectureItem first, WarehouseMapArchitectureItem second) =>
        SameEditableValues(first, second) && first.ZIndex == second.ZIndex
        && first.GroupId == second.GroupId && first.IsArchived == second.IsArchived;

    private async Task<IReadOnlyList<WarehouseMapReviewWarning>> BuildWarningsAsync(
        IReadOnlyList<WarehouseMapGeometry> operational,
        IReadOnlyList<WarehouseMapArchitectureItem> architecture,
        decimal? scaleUnitsPerInch,
        string measurementSystem,
        CancellationToken token)
    {
        var warnings = new List<WarehouseMapReviewWarning>();
        var active = architecture.Where(item => !item.IsArchived).ToArray();
        if (scaleUnitsPerInch is decimal scale)
        {
            foreach (var aisle in active.Where(item => item.LayerCode == "AISLES" && item.Kind == "Rectangle"))
            {
                var widthInches = Math.Min(aisle.Width, aisle.Height) / scale;
                if (widthInches < 32)
                    warnings.Add(new("NARROW_AISLE",
                        $"El pasillo mide {FormatDistance(widthInches, measurementSystem)} de ancho; 32 in es una referencia editorial, no normativa.",
                        [aisle.Id]));
            }
        }

        var zones = active.Where(item => item.LayerCode == "ZONES" && item.Kind == "Rectangle").ToArray();
        AddPairWarnings(zones.Select(item => (item.Id, Bounds(item))).ToArray(), "ZONE_OVERLAP",
            "Dos zonas arquitectónicas se superponen.", warnings);

        var kinds = await dbContext.WarehouseMapElements.AsNoTracking().Where(item => item.LayoutId == 1)
            .Select(item => new { item.Id, item.Kind }).ToDictionaryAsync(item => item.Id, item => item.Kind, token);
        var racks = operational.Where(item => kinds.GetValueOrDefault(item.Id) == WarehouseMapElementKind.Rack && item.IsVisible)
            .Select(item => (item.Id, Bounds(item))).ToArray();
        AddPairWarnings(racks, "RACK_OVERLAP", "Dos racks se superponen.", warnings);
        var aisles = active.Where(item => item.LayerCode == "AISLES" && item.Kind == "Rectangle")
            .Select(item => (item.Id, Bounds(item))).ToArray();
        foreach (var rack in racks)
        {
            foreach (var aisle in aisles)
            {
                if (Overlaps(rack.Item2, aisle.Item2))
                    warnings.Add(new("RACK_IN_AISLE", "Un rack invade un pasillo.", [rack.Id, aisle.Id]));
            }
        }
        return warnings;
    }

    private static void AddPairWarnings((Guid Id, MapBounds Bounds)[] items, string code, string message,
        ICollection<WarehouseMapReviewWarning> warnings)
    {
        for (var first = 0; first < items.Length; first++)
        {
            for (var second = first + 1; second < items.Length; second++)
            {
                if (Overlaps(items[first].Bounds, items[second].Bounds))
                    warnings.Add(new(code, message, [items[first].Id, items[second].Id]));
            }
        }
    }

    private readonly record struct MapBounds(decimal Left, decimal Top, decimal Right, decimal Bottom);
    private static MapBounds Bounds(WarehouseMapGeometry item) =>
        item.Rotation is 90 or 270
            ? new(item.X - item.Height, item.Y, item.X, item.Y + item.Width)
            : item.Rotation == 180
                ? new(item.X - item.Width, item.Y - item.Height, item.X, item.Y)
                : new(item.X, item.Y, item.X + item.Width, item.Y + item.Height);
    private static MapBounds Bounds(WarehouseMapArchitectureItem item) =>
        item.Rotation is 90 or 270
            ? new(item.X - item.Height, item.Y, item.X, item.Y + item.Width)
            : item.Rotation == 180
                ? new(item.X - item.Width, item.Y - item.Height, item.X, item.Y)
                : new(item.X, item.Y, item.X + item.Width, item.Y + item.Height);
    private static bool Overlaps(MapBounds first, MapBounds second) =>
        Math.Min(first.Right, second.Right) > Math.Max(first.Left, second.Left)
        && Math.Min(first.Bottom, second.Bottom) > Math.Max(first.Top, second.Top);

    public static string FormatDistance(decimal inches, string measurementSystem)
    {
        static string Number(decimal value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
        if (measurementSystem == "METRIC")
        {
            var centimeters = inches * 2.54m;
            return centimeters < 100 ? $"{Number(centimeters)} cm" : $"{Number(centimeters / 100)} m";
        }
        if (inches < 36) return $"{Number(inches)} in";
        var yards = decimal.Floor(inches / 36);
        var remainder = inches - yards * 36;
        return remainder < 0.005m ? $"{yards:0} yd" : $"{yards:0} yd {Number(remainder)} in";
    }

    private static (int SchemaVersion, string Summary) SummarizeRevision(string changesJson)
    {
        try
        {
            using var document = JsonDocument.Parse(changesJson);
            var root = document.RootElement;
            var schema = root.TryGetProperty("SchemaVersion", out var schemaValue) ? schemaValue.GetInt32() : 1;
            if (schema < 4 || !root.TryGetProperty("Architecture", out var architecture))
                return (schema, "La revisión histórica completa permanece disponible en la auditoría.");
            static int Count(JsonElement parent, string name) =>
                parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                    ? value.GetArrayLength()
                    : 0;
            var added = Count(architecture, "Added");
            var modified = Count(architecture, "Modified");
            var archived = Count(architecture, "Archived");
            var restored = Count(architecture, "Restored");
            return (schema, $"Arquitectura: {added} altas, {modified} modificaciones, {archived} archivados y {restored} restaurados.");
        }
        catch (JsonException)
        {
            return (0, "El detalle histórico permanece almacenado, pero su esquema no pudo resumirse.");
        }
    }

    private static WarehouseMapMeasurementSystem ParseMeasurementSystem(string value) =>
        value == "METRIC" ? WarehouseMapMeasurementSystem.Metric : WarehouseMapMeasurementSystem.Imperial;

    private static WarehouseMapGeometry ToGeometry(WarehouseMapElement item) => new(item.Id, item.X, item.Y, item.Width, item.Height, item.Rotation, item.ZIndex, item.IsVisible);
    private static WarehouseMapLayerState ToLayerState(WarehouseMapLayer item) =>
        new(WarehouseMapArchitectureCatalog.Code(item.Code), item.IsLocked);
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
