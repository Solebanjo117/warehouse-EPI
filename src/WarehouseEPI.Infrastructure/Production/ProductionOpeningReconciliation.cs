using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionImportCell(string Sheet, string Table, int Row, string Cell,
    string Header, string? Sku, string Text, string? Formula, decimal? Number, string State);
public sealed record ProductionOpeningResolution(Guid ProductId, decimal Cutting, decimal Sewing,
    decimal ReadyToPack, string Reason)
{
    public decimal For(ProductionDailyArea area) => area switch
    { ProductionDailyArea.Cutting => Cutting, ProductionDailyArea.Sewing => Sewing, _ => ReadyToPack };
}
public sealed record ProductionOpeningReview(Guid ProductId, string Sku, decimal Planned,
    IReadOnlyList<decimal> Completed, IReadOnlyList<decimal?> Closing,
    IReadOnlyList<ProductionDailyArea> ApplicableAreas, IReadOnlyList<string> Problems,
    IReadOnlyList<ProductionScheduleImportCarryover> Packages, ProductionOpeningResolution? Resolution,
    bool Resolved, IReadOnlyList<string> SourceSkus);

public sealed partial class ProductionScheduleImportService
{
    private sealed record OpeningReadResult(IReadOnlyList<ProductionOpeningReview> Reviews,
        IReadOnlyList<ProductionImportCell> Evidence, IReadOnlyList<string> Candidates);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string CanonicalHash(object value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(document.RootElement, writer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        static void Write(JsonElement element, Utf8JsonWriter writer)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Write(property.Value, writer); }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            { writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(item, writer); writer.WriteEndArray(); }
            else element.WriteTo(writer);
        }
    }

    private async Task<string> GetDependencyHashAsync(IEnumerable<ProductionScheduleImportWeek> weeks, CancellationToken token,
        IEnumerable<Guid>? reviewedProducts = null)
    {
        var ids = weeks.SelectMany(w => w.Lines.Select(x => x.ProductId).Concat(w.Captures.Select(x => x.ProductId))
            .Concat(w.FinalLines.Select(x => x.ProductId)).Concat(w.OpeningCarryovers.Select(x => x.ProductId)))
            .Concat(reviewedProducts ?? []).Distinct().ToArray();
        var config = await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        var products = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Sku, x.IsActive, x.BaseUnitId }).ToListAsync(token);
        var stages = await db.ProductionStages.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.IsActive }).ToListAsync(token);
        var shifts = await db.ProductionShifts.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.IsActive }).ToListAsync(token);
        return Hash(JsonSerializer.Serialize(new
        {
            config.CuttingStageId,
            config.SewingStageId,
            config.ReadyToPackStageId,
            config.Shift1Id,
            config.Shift2Id,
            config.Version,
            products,
            stages,
            shifts
        }));
    }

    private static ProductionImportCell ReadEvidence(IXLCell cell, string table, string header, string? sku)
    {
        // Never ask ClosedXML to recalculate a formula: the original cached result is evidence, not an inferred zero.
        var value = cell.HasFormula ? cell.CachedValue : cell.Value;
        var number = value.IsNumber ? (decimal?)value.GetNumber() : null;
        var state = value.IsError ? "Error" : cell.HasFormula && (value.IsBlank || value.IsText && value.GetText().Length == 0)
            ? "MissingFormulaResult" : value.IsBlank ? "Blank" : number.HasValue ? "Number" : "Text";
        return new(cell.Worksheet.Name, table, cell.Address.RowNumber, cell.Address.ToStringRelative(), header, sku,
            value.ToString(CultureInfo.InvariantCulture), cell.HasFormula ? cell.FormulaA1 : null, number, state);
    }

    private static string ClosingHeader(string value) => Normalize(value) switch
    {
        "cortependientefinal" or "cuttingpendingfinal" => "cuttingpendingfinal",
        "costurapendientefinal" or "sewingpendingfinal" => "sewingpendingfinal",
        "readytopackpendientefinal" or "readytopackpendingfinal" => "readytopackpendingfinal",
        _ => Normalize(value)
    };

    private static bool ClosingName(string name)
    {
        var normalized = Normalize(name);
        return new[] { "pendienteproximasemana", "pendingnextweek" }
            .Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal) && normalized[prefix.Length..].All(char.IsDigit));
    }

    private async Task<OpeningReadResult> ReconcileOpeningAsync(XLWorkbook workbook, List<ProductionScheduleImportWeek> weeks,
        ParseContext context, ProductionScheduleImportResolutions resolutions, CancellationToken token)
    {
        var prior = weeks.SingleOrDefault(x => x.WeekStart == new DateOnly(2026, 9, 14));
        var draft = weeks.SingleOrDefault(x => x.IsDraft);
        if (prior is null || draft is null) return new([], [], []);
        var sheet = workbook.Worksheet(prior.Sheet);
        var evidence = new List<ProductionImportCell>();
        foreach (var source in weeks)
            foreach (var table in workbook.Worksheet(source.Sheet).Tables)
            {
                var hs = table.HeadersRow().Cells().Select(c => c.GetString()).ToArray();
                var skuColumn = Array.FindIndex(hs, h => Normalize(h) is "partnumber" or "item");
                if (skuColumn < 0 || table.DataRange is null) continue;
                foreach (var row in table.DataRange.Rows())
                {
                    var sku = Text(row.Cell(skuColumn + 1));
                    if (string.IsNullOrWhiteSpace(sku) || Normalize(sku) is "total" or "totals") continue;
                    for (var c = 0; c < hs.Length; c++)
                        if (IsOpeningEvidenceHeader(hs[c])) evidence.Add(ReadEvidence(row.Cell(c + 1), table.Name, hs[c], sku));
                }
            }
        var candidates = sheet.Tables.Where(t => ClosingName(t.Name)).ToArray();
        var closing = resolutions.ClosingTable is null ? (candidates.Length == 1 ? candidates[0] : null)
            : candidates.SingleOrDefault(t => t.Name == resolutions.ClosingTable);
        if (resolutions.ClosingTable is not null && closing is null || candidates.Length > 1 && closing is null)
            context.Issue(prior.Sheet, null, "Selecciona una tabla de cierre válida.", ProductionScheduleImportIssueKind.OpeningReconciliation,
                ProductionScheduleImportTable.Carryover);
        var closingRows = new Dictionary<Guid, List<(int Row, decimal?[] Values, string Sku)>>();
        var closingColumns = closing?.HeadersRow().Cells().Select((c, i) => (Key: ClosingHeader(c.GetString()), Column: i + 1))
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Column);
        var keys = new[] { "cuttingpendingfinal", "sewingpendingfinal", "readytopackpendingfinal" };
        var validClosing = closingColumns is not null && closingColumns.ContainsKey("item") && keys.All(closingColumns.ContainsKey);
        if (closing is not null && !validClosing)
            context.Issue(prior.Sheet, null, "El cierre no tiene las columnas de pendientes requeridas.",
                ProductionScheduleImportIssueKind.OpeningReconciliation, ProductionScheduleImportTable.Carryover);
        if (validClosing && closing!.DataRange is not null)
            foreach (var row in closing.DataRange.Rows())
            {
                var sku = Text(row.Cell(closingColumns!["item"]));
                if (string.IsNullOrWhiteSpace(sku) || Normalize(sku) is "total" or "totals") continue;
                var id = context.Product(sku);
                if (id is null)
                {
                    context.Issue(prior.Sheet, row.RowNumber(), $"SKU de arrastre sin resolver: {sku}.",
                        ProductionScheduleImportIssueKind.UnknownSku, ProductionScheduleImportTable.Carryover, sku);
                    continue;
                }
                if (!closingRows.TryGetValue(id.Value, out var rows)) closingRows[id.Value] = rows = [];
                rows.Add((row.RowNumber(), keys.Select(k => ReadEvidence(row.Cell(closingColumns[k]), closing.Name, k, sku).Number).ToArray(), sku));
            }
        var ids = prior.Lines.Select(x => x.ProductId).Concat(prior.Captures.Select(x => x.ProductId))
            .Concat(closingRows.Keys).Concat(draft.Lines.Where(x => x.IsCarryover).Select(x => x.ProductId)).Distinct().Order().ToArray();
        var products = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
        var reviews = new List<ProductionOpeningReview>();
        foreach (var invalid in resolutions.Opening.GroupBy(x => x.ProductId).Where(g => g.Count() != 1 || !ids.Contains(g.Key)))
            context.Issue(prior.Sheet, null, "La resolución de apertura contiene un producto inválido o repetido.",
                ProductionScheduleImportIssueKind.OpeningReconciliation, ProductionScheduleImportTable.Carryover);
        var areas = Enum.GetValues<ProductionDailyArea>();
        foreach (var id in ids)
        {
            var lines = prior.Lines.Where(x => x.ProductId == id).ToArray();
            var captures = prior.Captures.Where(x => x.ProductId == id).ToArray();
            var sourceSkus = lines.Select(x => x.Sku).Concat(captures.Select(x => x.Sku))
                .Concat(closingRows.GetValueOrDefault(id)?.Select(x => x.Sku) ?? [])
                .Concat(draft.Lines.Where(x => x.ProductId == id && x.IsCarryover).Select(x => x.Sku)).Distinct().Order().ToArray();
            var problems = new List<string>();
            var rowSet = closingRows.GetValueOrDefault(id);
            var pending = rowSet?.Count == 1 ? rowSet[0].Values : new decimal?[3];
            var rowNumber = rowSet?.FirstOrDefault().Row ?? 0;
            // "Pendiente próxima semana" is taken as the operators closed it: historical data is not reconciled.
            // Only a closing that cannot be read stops the import; a product missing from it opens at zero.
            if (!validClosing) problems.Add("Falta un cierre válido; define la apertura manualmente.");
            else if (rowSet?.Count > 1) problems.Add("El producto aparece varias veces en el cierre.");
            else if (rowSet?.Count == 1 && pending.Any(x => !x.HasValue))
                problems.Add("El cierre contiene pendientes vacíos o no numéricos.");
            var completed = areas.Select(a => captures.Where(c => c.Area == a).Sum(c => c.Quantity)).ToArray();
            var packages = rowSet?.Count == 1 && problems.Count == 0 ? ClosingPackages(products[id].Sku, id, pending, rowNumber) : [];
            var resolution = resolutions.Opening.FirstOrDefault(x => x.ProductId == id);
            var resolved = problems.Count == 0;
            if (resolution is not null)
            {
                var quantities = areas.Select(resolution.For).ToArray();
                var valid = !string.IsNullOrWhiteSpace(resolution.Reason) && resolution.Reason.Trim().Length <= 500 &&
                    quantities.All(q => q >= 0 && q <= 99999999999999.9999m && decimal.Round(q, 4) == q);
                if (!valid) problems.Add("La resolución requiere motivo y cantidades válidas.");
                else
                {
                    packages = areas.Where(a => resolution.For(a) > 0).Select(a => new ProductionScheduleImportCarryover(
                        products[id].Sku, id, a, resolution.For(a), rowNumber)).ToList();
                    resolved = true;
                }
                if (!valid) resolved = false;
            }
            if (!resolved)
            {
                packages.Clear();
                context.Issue(prior.Sheet, rowNumber > 0 ? rowNumber : null, $"Apertura pendiente de conciliación: {products[id].Sku}.",
                    ProductionScheduleImportIssueKind.OpeningReconciliation, ProductionScheduleImportTable.Carryover, products[id].Sku);
            }
            reviews.Add(new(id, products[id].Sku, lines.Sum(x => x.Quantity), completed, pending, areas,
                problems, packages, resolution, resolved, sourceSkus));
        }
        var allPackages = reviews.SelectMany(x => x.Packages).OrderBy(x => x.Sku, StringComparer.Ordinal).ThenBy(x => x.Area).ToArray();
        var finalLines = draft.Lines.Where(x => !x.IsCarryover).Concat(allPackages.Select(x => new ProductionScheduleImportLine(
            draft.WeekStart, x.Sku, x.ProductId, x.Quantity, null, null, null,
            $"Arrastre de apertura desde {prior.Sheet} · {x.Area}", true, x.SourceRow, x.Area, prior.Sheet))).ToArray();
        weeks[weeks.IndexOf(draft)] = draft with { OpeningCarryovers = allPackages, FinalLines = finalLines };
        return new(reviews.OrderBy(x => x.Sku, StringComparer.Ordinal).ToArray(), evidence, candidates.Select(x => x.Name).ToArray());
    }

    // Ready to Pack pending is the unfinished total and an earlier area cannot hold more than the next one, so any
    // excess the operators left in Corte or Costura is dropped instead of reconciled. Negative pending counts as zero.
    private static List<ProductionScheduleImportCarryover> ClosingPackages(string sku, Guid productId, decimal?[] pending, int row)
    {
        var values = pending.Select(x => Math.Max(0, decimal.Round(x!.Value, 4, MidpointRounding.AwayFromZero))).ToArray();
        var ready = values[(int)ProductionDailyArea.ReadyToPack];
        var sewing = Math.Min(values[(int)ProductionDailyArea.Sewing], ready);
        var cutting = Math.Min(values[(int)ProductionDailyArea.Cutting], sewing);
        return new[] { (Area: ProductionDailyArea.Cutting, Quantity: cutting), (Area: ProductionDailyArea.Sewing, Quantity: sewing - cutting),
                (Area: ProductionDailyArea.ReadyToPack, Quantity: ready - sewing) }
            .Where(x => x.Quantity > 0).Select(x => new ProductionScheduleImportCarryover(sku, productId, x.Area, x.Quantity, row)).ToList();
    }

    // Persist source cells that explain a quantity, an area, a route start, or an operator note. Decorative table output is not evidence.
    private static bool IsOpeningEvidenceHeader(string header) => Normalize(header) switch
    {
        "item" or "partnumber" or "day" or "date" or "qty" or "cantidad" or "area" or "shift" or
        "piecescompleted" or "piezascompletadas" or "reportedby" or "reportadopor" or "notes" or "notas" or
        "comments" or "comentarios" or "tipo" or "type" or "column1" or "column2" or "programadonuevo" or
        "arrastresemanapasada" or "arrastrecorte" or "arrastrecostura" or "arrastrereadytopack" or
        "cortependientefinal" or "cuttingpendingfinal" or "costurapendientefinal" or "sewingpendingfinal" or
        "readytopackpendientefinal" or "readytopackpendingfinal" => true,
        _ => false
    };
}
