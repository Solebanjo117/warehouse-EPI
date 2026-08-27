using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Labels;

public sealed record LabelTemplateChoice(Guid TemplateId, Guid VersionId, string Code, string Name, int Version, LabelSizePreset SizePreset, LabelTemplateKind Kind);
public sealed record LabelTemplateAdminRow(Guid TemplateId, string Code, Guid VersionId, string Name, int Version, LabelTemplateStatus Status, bool IsCurrent, DateTimeOffset UpdatedAt, LabelTemplateKind Kind);
public sealed record LabelVersionEditor(Guid TemplateId, string Code, Guid VersionId, string Name, int Version, LabelSizePreset SizePreset, LabelTemplateStatus Status, string DesignJson, uint RowVersion, bool IsCurrent, LabelTemplateKind Kind, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);
public enum LabelTemplateMutationStatus { Success, NotFound, Unauthorized, Invalid, Conflict, InvalidPin }
public sealed record LabelTemplateMutationResult(LabelTemplateMutationStatus Status, Guid? VersionId = null, IReadOnlyList<string>? Errors = null);
public sealed record LabelAssetView(Guid Id, string Name, string ContentType, int Width, int Height, bool IsArchived, DateTimeOffset CreatedAt);
public sealed record LabelAssetContent(byte[] Content, string ContentType, string Sha256);

public sealed class LabelTemplateService(WarehouseDbContext db, UserPinService pins, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<LabelTemplateChoice>> GetPublishedAsync(LabelTemplateKind kind = LabelTemplateKind.ProductLabel, CancellationToken token = default) =>
        await db.LabelTemplates.AsNoTracking().Where(item => item.CurrentPublishedVersionId != null && item.Kind == kind)
            .OrderBy(item => item.CurrentPublishedVersion!.Name)
            .Select(item => new LabelTemplateChoice(item.Id, item.CurrentPublishedVersionId!.Value, item.Code,
                item.CurrentPublishedVersion!.Name, item.CurrentPublishedVersion.Version, item.CurrentPublishedVersion.SizePreset, item.Kind))
            .ToListAsync(token);

    public async Task<LabelTemplateChoice?> GetPublishedAsync(Guid versionId, LabelTemplateKind kind = LabelTemplateKind.ProductLabel, CancellationToken token = default) =>
        await db.LabelTemplates.AsNoTracking().Where(item => item.CurrentPublishedVersionId == versionId && item.Kind == kind)
            .Select(item => new LabelTemplateChoice(item.Id, versionId, item.Code, item.CurrentPublishedVersion!.Name,
                item.CurrentPublishedVersion.Version, item.CurrentPublishedVersion.SizePreset, item.Kind)).SingleOrDefaultAsync(token);

    public async Task<LabelTemplateChoice?> GetPublishedByCodeAsync(string code, LabelTemplateKind kind = LabelTemplateKind.ProductLabel, CancellationToken token = default) =>
        await db.LabelTemplates.AsNoTracking().Where(item => item.Code == code && item.CurrentPublishedVersionId != null && item.Kind == kind)
            .Select(item => new LabelTemplateChoice(item.Id, item.CurrentPublishedVersionId!.Value, item.Code,
                item.CurrentPublishedVersion!.Name, item.CurrentPublishedVersion.Version, item.CurrentPublishedVersion.SizePreset, item.Kind)).SingleOrDefaultAsync(token);

    public async Task<LabelTemplateVersion?> GetPublishedEntityAsync(Guid versionId, CancellationToken token = default) =>
        await db.LabelTemplateVersions.AsNoTracking().Include(item => item.Template)
            .SingleOrDefaultAsync(item => item.Id == versionId && item.Template.CurrentPublishedVersionId == versionId && item.Status == LabelTemplateStatus.Published, token);

    public async Task<IReadOnlyList<LabelTemplateAdminRow>> GetAdminRowsAsync(CancellationToken token = default) =>
        await db.LabelTemplateVersions.AsNoTracking()
            .OrderBy(item => item.Template.Code).ThenByDescending(item => item.Version)
            .Select(item => new LabelTemplateAdminRow(item.TemplateId, item.Template.Code,
            item.Id, item.Name, item.Version, item.Status, item.Template.CurrentPublishedVersionId == item.Id, item.UpdatedAt, item.Template.Kind))
            .ToListAsync(token);

    public async Task<LabelVersionEditor?> GetEditorAsync(Guid versionId, CancellationToken token = default)
    {
        var row = await db.LabelTemplateVersions.AsNoTracking().Where(item => item.Id == versionId)
            .Select(item => new { item.TemplateId, item.Template.Code, item.Template.Kind, item.Id, item.Name, item.Version, item.SizePreset, item.Status, item.DesignJson, item.RowVersion, IsCurrent = item.Template.CurrentPublishedVersionId == item.Id }).SingleOrDefaultAsync(token);
        if (row is null) return null;
        var validation = LabelDesignSerializer.Validate(LabelDesignSerializer.Deserialize(row.DesignJson), row.SizePreset, row.Kind);
        return new(row.TemplateId, row.Code, row.Id, row.Name, row.Version, row.SizePreset, row.Status, row.DesignJson, row.RowVersion, row.IsCurrent, row.Kind, validation.Errors, validation.Warnings);
    }

    public async Task<LabelTemplateMutationResult> CreateAsync(Guid userId, string code, string name, LabelSizePreset size, CancellationToken token = default)
    {
        code = CatalogNormalization.NormalizeCode(code);
        name = name.Trim();
        if (userId == Guid.Empty || !System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z0-9][A-Z0-9-]{2,59}$") || name.Length is < 1 or > 120 || !LabelSizeRegistry.All.Any(item => item.Preset == size))
            return new(LabelTemplateMutationStatus.Invalid, Errors: ["Código, nombre o tamaño no válido."]);
        if (await db.LabelTemplates.AnyAsync(item => item.Code == code, token)) return new(LabelTemplateMutationStatus.Invalid, Errors: ["Ya existe una plantilla con ese código."]);
        var now = timeProvider.GetUtcNow();
        var template = new LabelTemplate { Code = code, CreatedAt = now, UpdatedAt = now };
        var version = new LabelTemplateVersion { Template = template, Version = 1, Name = name, SizePreset = size, Status = LabelTemplateStatus.Draft, DesignJson = LabelDesignSerializer.Serialize(new LabelDesignDocumentV1 { Elements = [new() { Type = LabelElementType.Text, X = 200, Y = 200, Width = 1200, Height = 300, Text = "Nueva etiqueta", Bold = true, FontSize = 18 }] }), CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now };
        db.AddRange(template, version, Event(template, version, LabelTemplateEventType.Created, userId, now));
        await db.SaveChangesAsync(token);
        return new(LabelTemplateMutationStatus.Success, version.Id);
    }

    public async Task<LabelTemplateMutationResult> SaveAsync(Guid userId, Guid versionId, string name, LabelSizePreset size, string designJson, uint expectedRowVersion, bool acknowledgeWarnings, CancellationToken token = default)
    {
        var version = await db.LabelTemplateVersions.Include(item => item.Template).Include(item => item.Assets).SingleOrDefaultAsync(item => item.Id == versionId, token);
        if (version is null) return new(LabelTemplateMutationStatus.NotFound);
        if (version.Status is not (LabelTemplateStatus.Draft or LabelTemplateStatus.InValidation)) return new(LabelTemplateMutationStatus.Invalid, Errors: ["La versión publicada o retirada no puede modificarse."]);
        if (version.RowVersion != expectedRowVersion) return new(LabelTemplateMutationStatus.Conflict, Errors: ["La plantilla cambió mientras estaba abierta. Recarga antes de guardar."]);
        name = name.Trim();
        if (name.Length is < 1 or > 120) return new(LabelTemplateMutationStatus.Invalid, Errors: ["El nombre debe contener entre 1 y 120 caracteres."]);
        var document = LabelDesignSerializer.Deserialize(designJson);
        var validation = LabelDesignSerializer.Validate(document, size, version.Template.Kind);
        if (!validation.IsValid) return new(LabelTemplateMutationStatus.Invalid, Errors: validation.Errors);
        if (version.Status == LabelTemplateStatus.InValidation && validation.Warnings.Count > 0 && !acknowledgeWarnings)
            return new(LabelTemplateMutationStatus.Invalid, Errors: ["Confirma las advertencias antes de guardar una versión en validación."]);
        var assetIds = document!.Elements.Where(item => item.AssetId != null).Select(item => item.AssetId!.Value).Distinct().ToArray();
        if (assetIds.Length != await db.LabelAssets.CountAsync(item => assetIds.Contains(item.Id), token)) return new(LabelTemplateMutationStatus.Invalid, Errors: ["El diseño contiene imágenes inexistentes."]);
        version.Name = name; version.SizePreset = size; version.DesignJson = LabelDesignSerializer.Serialize(document); version.UpdatedAt = timeProvider.GetUtcNow();
        version.Assets.Clear(); foreach (var assetId in assetIds) version.Assets.Add(new() { TemplateVersionId = version.Id, AssetId = assetId });
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return new(LabelTemplateMutationStatus.Conflict, Errors: ["La plantilla cambió mientras estaba abierta."]); }
        return new(LabelTemplateMutationStatus.Success, version.Id);
    }

    public async Task<LabelTemplateMutationResult> SubmitAsync(Guid userId, Guid versionId, bool acknowledgeWarnings, CancellationToken token = default)
    {
        var version = await db.LabelTemplateVersions.Include(item => item.Template).SingleOrDefaultAsync(item => item.Id == versionId, token);
        if (version is null) return new(LabelTemplateMutationStatus.NotFound);
        if (version.Status != LabelTemplateStatus.Draft) return new(LabelTemplateMutationStatus.Invalid, Errors: ["Solo un borrador puede enviarse a validación."]);
        var validation = LabelDesignSerializer.Validate(LabelDesignSerializer.Deserialize(version.DesignJson), version.SizePreset, version.Template.Kind);
        if (!validation.IsValid || (validation.Warnings.Count > 0 && !acknowledgeWarnings)) return new(LabelTemplateMutationStatus.Invalid, Errors: validation.Errors.Concat(validation.Warnings.Count > 0 && !acknowledgeWarnings ? ["Confirma las advertencias para continuar."] : []).ToArray());
        version.Status = LabelTemplateStatus.InValidation; version.UpdatedAt = timeProvider.GetUtcNow();
        db.Add(Event(version.Template, version, LabelTemplateEventType.Submitted, userId, version.UpdatedAt));
        await db.SaveChangesAsync(token); return new(LabelTemplateMutationStatus.Success, version.Id);
    }

    public async Task<LabelTemplateMutationResult> ReturnToDraftAsync(Guid userId, Guid versionId, CancellationToken token = default)
    {
        var version = await db.LabelTemplateVersions.Include(item => item.Template).SingleOrDefaultAsync(item => item.Id == versionId, token);
        if (version is null) return new(LabelTemplateMutationStatus.NotFound);
        if (version.Status != LabelTemplateStatus.InValidation) return new(LabelTemplateMutationStatus.Invalid);
        version.Status = LabelTemplateStatus.Draft; version.UpdatedAt = timeProvider.GetUtcNow();
        db.Add(Event(version.Template, version, LabelTemplateEventType.ReturnedToDraft, userId, version.UpdatedAt));
        await db.SaveChangesAsync(token); return new(LabelTemplateMutationStatus.Success, version.Id);
    }

    public async Task<LabelTemplateMutationResult> PublishAsync(Guid userId, Guid versionId, bool acknowledgeWarnings, CancellationToken token = default)
    {
        var version = await db.LabelTemplateVersions.Include(item => item.Template).SingleOrDefaultAsync(item => item.Id == versionId, token);
        if (version is null) return new(LabelTemplateMutationStatus.NotFound);
        if (version.Status != LabelTemplateStatus.InValidation) return new(LabelTemplateMutationStatus.Invalid, Errors: ["La versión debe estar en validación."]);
        var validation = LabelDesignSerializer.Validate(LabelDesignSerializer.Deserialize(version.DesignJson), version.SizePreset, version.Template.Kind);
        if (!validation.IsValid || (validation.Warnings.Count > 0 && !acknowledgeWarnings)) return new(LabelTemplateMutationStatus.Invalid, Errors: validation.Errors.Concat(validation.Warnings.Count > 0 && !acknowledgeWarnings ? ["Confirma las advertencias antes de publicar."] : []).ToArray());
        var now = timeProvider.GetUtcNow(); version.Status = LabelTemplateStatus.Published; version.PublishedByUserId = userId; version.PublishedAt = now; version.UpdatedAt = now;
        version.Template.CurrentPublishedVersionId = version.Id; version.Template.UpdatedAt = now;
        db.Add(Event(version.Template, version, LabelTemplateEventType.Published, userId, now));
        await db.SaveChangesAsync(token); return new(LabelTemplateMutationStatus.Success, version.Id);
    }

    public async Task<LabelTemplateMutationResult> DuplicateAsync(Guid userId, Guid publishedVersionId, CancellationToken token = default)
    {
        var source = await db.LabelTemplateVersions.AsNoTracking().Include(item => item.Template).Include(item => item.Assets).SingleOrDefaultAsync(item => item.Id == publishedVersionId && item.Status == LabelTemplateStatus.Published, token);
        if (source is null) return new(LabelTemplateMutationStatus.NotFound);
        if (await db.LabelTemplateVersions.AnyAsync(item => item.TemplateId == source.TemplateId && (item.Status == LabelTemplateStatus.Draft || item.Status == LabelTemplateStatus.InValidation), token)) return new(LabelTemplateMutationStatus.Invalid, Errors: ["La plantilla ya tiene una versión editable."]);
        var now = timeProvider.GetUtcNow();
        var version = new LabelTemplateVersion { TemplateId = source.TemplateId, Version = await db.LabelTemplateVersions.Where(item => item.TemplateId == source.TemplateId).MaxAsync(item => item.Version, token) + 1, Name = source.Name, SizePreset = source.SizePreset, Status = LabelTemplateStatus.Draft, DesignJson = source.DesignJson, CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now };
        foreach (var asset in source.Assets) version.Assets.Add(new() { AssetId = asset.AssetId });
        db.Add(version); db.Add(Event(source.Template, version, LabelTemplateEventType.Duplicated, userId, now));
        await db.SaveChangesAsync(token); return new(LabelTemplateMutationStatus.Success, version.Id);
    }

    public async Task<LabelTemplateMutationResult> RetireAsync(Guid userId, Guid versionId, string pin, string reason, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500) return new(LabelTemplateMutationStatus.Invalid, Errors: ["El motivo de retiro es obligatorio."]);
        var authorized = await pins.AuthenticateAsync(pin, token);
        if (authorized?.Role.Code != "ADMIN") return new(LabelTemplateMutationStatus.InvalidPin);
        var version = await db.LabelTemplateVersions.Include(item => item.Template).SingleOrDefaultAsync(item => item.Id == versionId, token);
        if (version is null) return new(LabelTemplateMutationStatus.NotFound);
        if (version.Template.CurrentPublishedVersionId != version.Id || version.Status != LabelTemplateStatus.Published) return new(LabelTemplateMutationStatus.Invalid, Errors: ["Solo puede retirarse la versión publicada vigente."]);
        var now = timeProvider.GetUtcNow(); version.Status = LabelTemplateStatus.Retired; version.RetiredAt = now; version.RetiredByUserId = authorized.Id; version.Template.CurrentPublishedVersionId = null; version.Template.UpdatedAt = now;
        db.Add(new LabelTemplateEvent { TemplateId = version.TemplateId, TemplateVersionId = version.Id, Type = LabelTemplateEventType.Retired, RequestedByUserId = userId, AuthorizedByUserId = authorized.Id, Reason = reason.Trim(), RecordedAt = now });
        await db.SaveChangesAsync(token); return new(LabelTemplateMutationStatus.Success, version.Id);
    }

    private static LabelTemplateEvent Event(LabelTemplate template, LabelTemplateVersion version, LabelTemplateEventType type, Guid? userId, DateTimeOffset at) => new() { TemplateId = template.Id, TemplateVersionId = version.Id, Type = type, RequestedByUserId = userId, RecordedAt = at };
}

public sealed class LabelAssetService(WarehouseDbContext db, TimeProvider timeProvider)
{
    public const int MaxBytes = 1024 * 1024;
    public async Task<IReadOnlyList<LabelAssetView>> GetAllAsync(CancellationToken token = default) => await db.LabelAssets.AsNoTracking().OrderBy(item => item.IsArchived).ThenBy(item => item.Name).Select(item => new LabelAssetView(item.Id, item.Name, item.ContentType, item.Width, item.Height, item.IsArchived, item.CreatedAt)).ToListAsync(token);
    public async Task<LabelAssetContent?> GetContentAsync(Guid id, bool admin, CancellationToken token = default)
    {
        var query = db.LabelAssets.AsNoTracking().Where(item => item.Id == id);
        if (!admin) query = query.Where(item => item.Versions.Any(link => link.TemplateVersion.Template.CurrentPublishedVersionId == link.TemplateVersionId));
        return await query.Select(item => new LabelAssetContent(item.Content, item.ContentType, item.Sha256)).SingleOrDefaultAsync(token);
    }
    public async Task<(LabelAssetView? Asset, string? Error)> UploadAsync(Guid userId, string name, string contentType, byte[] content, CancellationToken token = default)
    {
        if (content.Length is < 1 or > MaxBytes) return (null, "La imagen debe pesar como máximo 1 MiB.");
        var dimensions = ImageDimensions(content, contentType);
        if (dimensions is null || dimensions.Value.Width is < 1 or > 4096 || dimensions.Value.Height is < 1 or > 4096) return (null, "La imagen no es PNG/JPEG válida o excede 4096×4096.");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var existing = await db.LabelAssets.SingleOrDefaultAsync(item => item.Sha256 == hash, token);
        if (existing is not null) return (new(existing.Id, existing.Name, existing.ContentType, existing.Width, existing.Height, existing.IsArchived, existing.CreatedAt), null);
        var safeName = Path.GetFileName(name).Trim();
        if (safeName.Length == 0) safeName = "imagen";
        safeName = safeName[..Math.Min(safeName.Length, 120)];
        var asset = new LabelAsset { Name = safeName, ContentType = contentType, Content = content, Sha256 = hash, Width = dimensions.Value.Width, Height = dimensions.Value.Height, CreatedByUserId = userId, CreatedAt = timeProvider.GetUtcNow() };
        db.Add(asset); await db.SaveChangesAsync(token); return (new(asset.Id, asset.Name, asset.ContentType, asset.Width, asset.Height, false, asset.CreatedAt), null);
    }
    public async Task<bool> SetArchivedAsync(Guid id, bool archived, CancellationToken token = default) { var asset = await db.LabelAssets.SingleOrDefaultAsync(item => item.Id == id, token); if (asset is null) return false; asset.IsArchived = archived; await db.SaveChangesAsync(token); return true; }

    private static (int Width, int Height)? ImageDimensions(byte[] data, string contentType)
    {
        if (contentType == "image/png")
        {
            if (data.Length < 45 || !data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(8, 4)) != 13 ||
                !data.AsSpan(12, 4).SequenceEqual("IHDR"u8) || !data.AsSpan(data.Length - 12, 8).SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68 }) ||
                data[26] != 0 || data[27] != 0 || data[28] > 1)
                return null;
            var hasImageData = false;
            for (var offset = 8; offset < data.Length;)
            {
                if (offset + 12 > data.Length) return null;
                var chunkLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
                if (chunkLength < 0 || offset + 12L + chunkLength > data.Length) return null;
                if (data.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8)) hasImageData = true;
                offset += 12 + chunkLength;
                if (offset == data.Length && !hasImageData) return null;
            }
            return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4)));
        }
        if (contentType != "image/jpeg" || data.Length < 12 || data[0] != 0xff || data[1] != 0xd8 || data[^2] != 0xff || data[^1] != 0xd9) return null;
        for (var offset = 2; offset + 8 < data.Length;)
        {
            if (data[offset++] != 0xff) return null; var marker = data[offset++];
            if (marker is 0xd8 or 0xd9) continue;
            if (offset + 2 > data.Length) return null; var length = (data[offset] << 8) + data[offset + 1];
            if (length < 2 || offset + length > data.Length) return null;
            if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
                return ((data[offset + 5] << 8) + data[offset + 6], (data[offset + 3] << 8) + data[offset + 4]);
            offset += length;
        }
        return null;
    }
}

public sealed record LabelRenderedElement(LabelElementDefinition Definition, string? Text, BarcodeSvg? Barcode, string? ImageUrl);
public sealed record LabelRenderDocument(string TemplateCode, string TemplateName, int TemplateVersion, LabelSizeDefinition Size, int Copies, IReadOnlyList<LabelRenderedElement> Elements);
public sealed record LabelGenerationResult(LabelRenderDocument? Document, IReadOnlyList<string> Errors);

public sealed class LabelDocumentService(BarcodeRenderingService barcodes)
{
    public LabelGenerationResult Render(LabelTemplateVersion version, OperationalProductResult product, IReadOnlyDictionary<string, string> submitted, int copies, IReadOnlyDictionary<string, string>? systemValues = null)
    {
        var errors = new List<string>(); if (copies is < 1 or > 100) errors.Add("Las copias deben estar entre 1 y 100.");
        var design = LabelDesignSerializer.Deserialize(version.DesignJson); var validation = LabelDesignSerializer.Validate(design, version.SizePreset, version.Template.Kind); errors.AddRange(validation.Errors);
        if (design is null) return new(null, errors);
        var fields = design.Fields.ToDictionary(item => item.Key, StringComparer.Ordinal); var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in submitted.Keys) if (!fields.ContainsKey(key) && !LabelDesignSerializer.IsSystemBinding(version.Template.Kind, key)) errors.Add($"El campo '{key}' no pertenece a la plantilla.");
        foreach (var field in design.Fields)
        {
            var value = submitted.GetValueOrDefault(field.Key)?.Trim() ?? field.DefaultValue?.Trim() ?? string.Empty;
            if (field.Required && value.Length == 0) errors.Add($"{field.Label} es obligatorio.");
            if (value.Length > 200) errors.Add($"{field.Label} excede 200 caracteres.");
            if (field.Type == LabelFieldType.Number && value.Length > 0)
            {
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)) errors.Add($"{field.Label} debe ser numérico.");
                else value = number.ToString("0.####", CultureInfo.InvariantCulture);
            }
            if (field.Type == LabelFieldType.Date && value.Length > 0)
            {
                if (!DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) errors.Add($"{field.Label} debe ser una fecha válida.");
                else value = date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
            }
            if (field.Type == LabelFieldType.Boolean && value.Length > 0 && value is not ("true" or "false")) errors.Add($"{field.Label} debe ser sí o no.");
            else if (field.Type == LabelFieldType.Boolean && value.Length > 0) value = value == "true" ? "YES" : "NO";
            if (field.Type == LabelFieldType.Select && value.Length > 0 && !field.Options.Contains(value, StringComparer.Ordinal)) errors.Add($"{field.Label} contiene una opción inválida.");
            normalized[field.Key] = value;
        }
        var quantityText = submitted.GetValueOrDefault("input.quantity")?.Trim() ?? string.Empty;
        if (design.Elements.Any(item => item.Binding == "input.quantity"))
        {
            if (!decimal.TryParse(quantityText, NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0 || decimal.Round(quantity, 4) != quantity) errors.Add("La cantidad debe ser positiva y admitir como máximo cuatro decimales.");
            else if (!product.AllowsDecimals && quantity != decimal.Truncate(quantity)) errors.Add($"La unidad {product.UnitCode} no admite decimales.");
            else quantityText = quantity.ToString("0.####", CultureInfo.InvariantCulture);
        }
        var dateText = submitted.GetValueOrDefault("input.manufacturingDate")?.Trim() ?? string.Empty;
        if (design.Elements.Any(item => item.Binding == "input.manufacturingDate") && !DateOnly.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) errors.Add("La fecha MFG es obligatoria y debe ser válida.");
        if (errors.Count > 0) return new(null, errors.Distinct().ToArray());
        string Value(string? binding, bool barcode = false) => binding switch
        {
            "product.sku" => product.Sku, "product.description" => product.Description ?? string.Empty, "product.unit" => product.UnitCode,
            "product.externalReference" => product.ExternalReference ?? string.Empty, "input.quantity" => barcode ? quantityText : $"{quantityText} {product.UnitCode}".Trim(),
            "input.manufacturingDate" => DateOnly.Parse(dateText, CultureInfo.InvariantCulture).ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
            "input.isRepack" => submitted.GetValueOrDefault("input.isRepack") == "true" ? "YES" : "NO",
            _ when binding is not null && systemValues is not null && systemValues.TryGetValue(binding, out var systemValue) => systemValue,
            _ => normalized.GetValueOrDefault(binding ?? string.Empty) ?? string.Empty
        };
        var rendered = new List<LabelRenderedElement>();
        foreach (var element in design.Elements.OrderBy(item => item.ZIndex))
        {
            BarcodeSvg? barcode = null; string? text = element.Type == LabelElementType.Text ? element.Text : element.Type == LabelElementType.Field ? Value(element.Binding) : null;
            if (element.Type == LabelElementType.Code128) { try { barcode = barcodes.RenderCode128Svg(Value(element.Binding, true), new(Math.Clamp(element.Width, 120, 2400), Math.Clamp(element.Height, 32, 800))); } catch (ArgumentException) { return new(null, [$"El valor de {element.Binding} no puede codificarse como Code 128."]); } }
            var imageUrl = element.BuiltInAssetKey == "extra-packaging-logo" ? LabelBuiltInAssets.ExtraPackagingLogoUrl : element.AssetId is null ? null : $"/Labels/Assets/{element.AssetId}";
            rendered.Add(new(element, text, barcode, imageUrl));
        }
        return new(new(version.Template.Code, version.Name, version.Version, LabelSizeRegistry.Get(version.SizePreset), copies, rendered), []);
    }
}
