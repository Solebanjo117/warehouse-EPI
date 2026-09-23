using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Web.Localization;
using WarehouseEPI.Web.Production;

namespace WarehouseEPI.Web.Pages.Admin.Production;

public sealed class ScheduleImportModel(
    ProductionImportDraftService drafts, ProductionDailyScheduleService daily, WarehouseDbContext db,
    IStringLocalizer<ProductionTexts> texts) : PageModel
{
    private const long MaxBytes = 15 * 1024 * 1024;
    // Keeps the resolve form below the default form value count limit (1024).
    public const int MaxRowFixes = 100;
    [BindProperty, Required(ErrorMessage = "Este campo es obligatorio.")] public IFormFile? Upload { get; set; }
    [BindProperty(SupportsGet = true)] public Guid PreviewToken { get; set; }
    [BindProperty] public int ExpectedRevision { get; set; }
    [BindProperty] public string Fingerprint { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public bool OnlyPending { get; set; } = true;
    [BindProperty(SupportsGet = true)] public int ReviewPage { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public Guid? EvidenceProduct { get; set; }
    [BindProperty(SupportsGet = true)] public int EvidencePage { get; set; } = 1;
    [BindProperty] public OpeningInput Opening { get; set; } = new();
    [BindProperty] public string? ClosingTable { get; set; }
    public ProductionImportDraftView? Draft { get; private set; }
    public Guid? CreatedWeekId { get; private set; }
    public IReadOnlyList<ProductionImportDraftSummary> SavedDrafts { get; private set; } = [];
    public IReadOnlyList<ProductionOpeningReview> ReviewRows { get; private set; } = [];

    public int OpeningReviewPage(ProductionOpeningReview review) => (Preview?.OpeningReview
        .Where(x => x.Sku.Contains(review.Sku, StringComparison.OrdinalIgnoreCase))
        .TakeWhile(x => x.ProductId != review.ProductId).Count() ?? 0) / 25 + 1;
    public int ReviewPages { get; private set; }
    public int ReviewTotal { get; private set; }
    public bool CanEdit => Draft?.Status is ProductionImportDraftStatus.Ready or ProductionImportDraftStatus.Reviewing;
    public bool CanConfirm => CanEdit && Draft?.IsCurrent == true && Preview?.CanConfirm == true;
    [BindProperty] public Guid OperationId { get; set; } = Guid.NewGuid();
    [BindProperty] public ScheduleModel.ConfigurationInput Configuration { get; set; } = new();
    [BindProperty] public ResolutionInput Resolution { get; set; } = new();
    public ProductionScheduleImportPreview? Preview { get; private set; }
    public ProductionDailySetup? Setup { get; private set; }
    public ProductionScheduleImportResolutions Applied { get; private set; } = ProductionScheduleImportResolutions.None;
    public IReadOnlyList<RowBlock> RowBlocks { get; private set; } = [];
    public int HiddenRowBlocks { get; private set; }
    public string? StatusMessage { get; private set; }
    public bool NeedsConfiguration => Preview?.Issues.Any(x => x.Kind == ProductionScheduleImportIssueKind.Configuration) == true;
    public bool CanResolve => Resolution.Areas.Count + Resolution.Shifts.Count + Resolution.Products.Count + RowBlocks.Count > 0;

    public async Task<IActionResult> OnGetAsync(CancellationToken token)
    {
        SavedDrafts = await drafts.ListAsync(Actor(), token);
        return PreviewToken == Guid.Empty ? Page() : await ValidateAsync(token);
    }

    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken token)
    {
        if (Upload is null || Upload.Length == 0 || Upload.Length > MaxBytes ||
            !string.Equals(Path.GetExtension(Upload.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(Upload), "Selecciona un archivo XLSX de hasta 15 MB.");
            ProductionDailyText.LocalizeErrors(ModelState, texts); return Page();
        }
        await using var input = Upload.OpenReadStream();
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, token);
        var bytes = buffer.ToArray();
        PreviewToken = await drafts.CreateAsync(Path.GetFileName(Upload.FileName), bytes, Actor(), token);
        ModelState.Clear();
        return await ValidateAsync(token);
    }

    public async Task<IActionResult> OnPostConfigureAsync(CancellationToken token)
    {
        var input = Configuration;
        ModelState.Clear();
        var draft = await EditableAsync(token);
        if (draft is null) return await ValidateAsync(token);
        var result = await daily.ConfigureAsync(new(input.OperationId, input.ExpectedVersion, input.CuttingStageId,
            input.SewingStageId, input.ReadyToPackStageId, input.Shift1Id, input.Shift2Id, Actor()), token);
        if (result.Success)
        {
            AddRevisionResult(await drafts.ReviseAsync(PreviewToken, ExpectedRevision, Actor(), draft.Resolutions, token: token));
            StatusMessage = texts["Configuración diaria guardada. Se volvió a validar el archivo."];
        }
        else AddErrors(result);
        return await ValidateAsync(token);
    }

    public async Task<IActionResult> OnPostResolveAsync(CancellationToken token)
    {
        var input = Resolution;
        ModelState.Clear();
        var draft = await EditableAsync(token);
        if (draft is null) return await ValidateAsync(token);
        AddRevisionResult(await drafts.ReviseAsync(PreviewToken, ExpectedRevision, Actor(), Merge(draft.Resolutions, input), token: token));
        return await ValidateAsync(token);
    }

    public async Task<IActionResult> OnPostDiscardAsync(CancellationToken token)
    {
        ModelState.Clear();
        var draft = await EditableAsync(token);
        if (draft is null) return await ValidateAsync(token);
        AddRevisionResult(await drafts.ReviseAsync(PreviewToken, ExpectedRevision, Actor(), ProductionScheduleImportResolutions.None, token: token));
        return await ValidateAsync(token);
    }

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken token)
    {
        ModelState.Clear();
        var result = await drafts.ConfirmAsync(new(PreviewToken, ExpectedRevision, Fingerprint, OperationId, Actor()), token);
        if (result.Success)
        {
            TempData["Success"] = texts["Histórico importado como solo lectura y semana actual creada en borrador con arrastre de apertura."].Value;
            return RedirectToPage(new { PreviewToken, OnlyPending = false });
        }
        ModelState.AddModelError(string.Empty, result.Errors?.FirstOrDefault() ?? "No fue posible confirmar la importación.");
        return await ValidateAsync(token);
    }

    public async Task<IActionResult> OnPostRevalidateAsync(CancellationToken token)
    {
        ModelState.Clear();
        var draft = await EditableAsync(token);
        if (draft is not null) AddRevisionResult(await drafts.ReviseAsync(PreviewToken, ExpectedRevision, Actor(), draft.Resolutions, token: token));
        return await ValidateAsync(token);
    }

    public async Task<IActionResult> OnPostDiscardDraftAsync(CancellationToken token)
    {
        ModelState.Clear();
        var draft = await EditableAsync(token);
        if (draft is not null) AddRevisionResult(await drafts.ReviseAsync(PreviewToken, ExpectedRevision, Actor(), draft.Resolutions, discard: true, token: token));
        return await ValidateAsync(token);
    }

    public async Task<IActionResult> OnPostDeleteDraftAsync(Guid draftId, CancellationToken token)
    {
        var result = await drafts.DeleteAsync(draftId, Actor(), token);
        if (result.Success) TempData["Success"] = texts["Borrador eliminado."].Value;
        else TempData["Error"] = ProductionDailyText.Message(texts, result.Errors?.FirstOrDefault() ?? "No fue posible completar la operación.");
        // Deleting another draft from the list keeps the open review; deleting the open one returns to the list.
        return RedirectToPage(PreviewToken == Guid.Empty || PreviewToken == draftId ? null : new { PreviewToken, OnlyPending = false });
    }

    public async Task<IActionResult> OnPostClosingAsync(CancellationToken token)
    {
        ModelState.Clear();
        var draft = await EditableAsync(token);
        if (draft is not null && draft.Preview.ClosingCandidates.Contains(ClosingTable))
            AddRevisionResult(await drafts.ReviseAsync(PreviewToken, ExpectedRevision, Actor(),
                draft.Resolutions with { ClosingTable = ClosingTable, Opening = [] }, token: token));
        else ModelState.AddModelError(string.Empty, texts["Selecciona una tabla de cierre válida."]);
        return await ValidateAsync(token);
    }

    public async Task<IActionResult> OnPostOpeningAsync(CancellationToken token)
    {
        ModelState.Clear();
        var draft = await EditableAsync(token);
        if (draft is null) return await ValidateAsync(token);
        var review = draft.Preview.OpeningReview.SingleOrDefault(x => x.ProductId == Opening.ProductId);
        if (review is null || !Quantity(Opening.Cutting, out var cut) || !Quantity(Opening.Sewing, out var sew) ||
            !Quantity(Opening.ReadyToPack, out var ready) || string.IsNullOrWhiteSpace(Opening.Reason) || Opening.Reason.Trim().Length > 500)
        {
            ModelState.AddModelError(string.Empty, texts["Escribe tres cantidades válidas con punto decimal y un motivo de hasta 500 caracteres."]);
            return await ValidateAsync(token);
        }
        var resolutions = draft.Resolutions with
        {
            Opening = draft.Resolutions.Opening.Where(x => x.ProductId != Opening.ProductId)
            .Append(new(Opening.ProductId, cut, sew, ready, Opening.Reason.Trim())).OrderBy(x => x.ProductId).ToArray()
        };
        AddRevisionResult(await drafts.ReviseAsync(PreviewToken, ExpectedRevision, Actor(), resolutions, token: token));
        return await ValidateAsync(token);
    }

    private static bool Quantity(string? text, out decimal value)
    {
        value = 0;
        return text is not null && global::System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+(\.\d{1,4})?$") &&
            decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && value <= 99999999999999.9999m;
    }

    private async Task<ProductionImportDraftView?> EditableAsync(CancellationToken token)
    {
        var draft = await drafts.GetAsync(PreviewToken, Actor(), token);
        if (draft is not null && draft.Version == ExpectedRevision && draft.Status is ProductionImportDraftStatus.Ready or ProductionImportDraftStatus.Reviewing)
            return draft;
        ModelState.AddModelError(string.Empty, texts["La revisión cambió o no está disponible. Recarga y vuelve a revisar antes de continuar."]);
        return null;
    }

    private void AddRevisionResult(ProductionDailyCommandResult result)
    {
        if (result.Success) StatusMessage = texts["Borrador guardado. Se volvió a validar el archivo."];
        else AddErrors(result);
    }

    public string AreaName(ProductionDailyArea area) => texts[area switch
    {
        ProductionDailyArea.Cutting => "Corte",
        ProductionDailyArea.Sewing => "Costura",
        _ => "Ready to Pack"
    }];

    public string ShiftName(int shift)
    {
        var id = shift == 1 ? Setup?.Configuration.Shift1Id : Setup?.Configuration.Shift2Id;
        var name = Setup?.Shifts.FirstOrDefault(x => x.Id == id)?.Name;
        return name is null ? $"T{shift}" : $"T{shift} · {name}";
    }

    public string TableName(ProductionScheduleImportTable table) => texts[table switch
    {
        ProductionScheduleImportTable.Plan => "Programa",
        ProductionScheduleImportTable.Execution => "Ejecución",
        _ => "Arrastre"
    }];

    public IEnumerable<string> AppliedDescriptions()
    {
        foreach (var (text, area) in Applied.Areas) yield return texts["Área \"{0}\" → {1}", text, AreaName(area)];
        foreach (var (text, shift) in Applied.Shifts) yield return texts["Turno \"{0}\" → {1}", text, ShiftName(shift)];
        foreach (var (text, _) in Applied.Products)
            yield return texts["SKU \"{0}\" → {1}", text, Resolution.ProductLabels.GetValueOrDefault(text) ?? "—"];
        foreach (var row in Applied.Rows)
        {
            var source = texts["{0} · {1} fila {2}", row.Sheet, TableName(row.Table), row.Row].Value;
            if (row.Skip) yield return texts["{0}: omitida", source];
            if (!row.Skip && row.Quantity is { } quantity)
                yield return texts["{0}: cantidad {1}", source, quantity.ToString(CultureInfo.InvariantCulture)];
            if (!row.Skip && row.Date is { } date) yield return texts["{0}: fecha {1}", source, date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)];
        }
    }

    private async Task<IActionResult> ValidateAsync(CancellationToken token)
    {
        Draft = await drafts.GetAsync(PreviewToken, Actor(), token);
        if (Draft is null) return NotFound();
        var resolutions = Draft.Resolutions;
        ExpectedRevision = Draft.Version;
        Fingerprint = Draft.ReviewedFingerprint;
        ClosingTable = resolutions.ClosingTable;
        ModelState.Remove(nameof(ExpectedRevision));
        ModelState.Remove(nameof(Fingerprint));
        Applied = resolutions;
        Preview = Draft.Preview;
        if (Draft.Status == ProductionImportDraftStatus.Confirmed)
            CreatedWeekId = await db.ProductionScheduleWeeks.AsNoTracking()
                .Where(x => x.WeekStart == new DateOnly(2026, 9, 21) &&
                    x.Origin == ProductionScheduleOrigin.ExcelImport && x.RequestFingerprint == Preview.FileHash)
                .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        var reviewRows = Preview.OpeningReview.Where(x => (!OnlyPending || !x.Resolved) &&
            (string.IsNullOrWhiteSpace(Search) || x.Sku.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
        ReviewTotal = reviewRows.Length;
        ReviewPages = Math.Max(1, (ReviewTotal + 24) / 25);
        ReviewPage = Math.Clamp(ReviewPage, 1, ReviewPages);
        ReviewRows = reviewRows.Skip((ReviewPage - 1) * 25).Take(25).ToArray();
        Setup = await ProductionDailySetup.LoadAsync(daily, db, token);
        Configuration = new()
        {
            ExpectedVersion = Setup.Configuration.Version,
            CuttingStageId = Setup.SuggestStage(Setup.Configuration.CuttingStageId, "CUT", "CUTTING", "CORTE"),
            SewingStageId = Setup.SuggestStage(Setup.Configuration.SewingStageId, "SEW", "SEWING", "COSTURA"),
            ReadyToPackStageId = Setup.SuggestStage(Setup.Configuration.ReadyToPackStageId, "RTP", "READY TO PACK", "LISTO PARA EMPACAR"),
            Shift1Id = Setup.SuggestShift(Setup.Configuration.Shift1Id, "T1", "SHIFT 1", "TURNO 1"),
            Shift2Id = Setup.SuggestShift(Setup.Configuration.Shift2Id, "T2", "SHIFT 2", "TURNO 2")
        };
        var issues = Preview.Issues;
        var linked = resolutions.Products.Values.Distinct().ToArray();
        var skus = await db.Products.AsNoTracking().Where(x => linked.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Sku, token);
        Resolution = new()
        {
            Areas = Texts(issues, ProductionScheduleImportIssueKind.UnknownArea).Select(x => new TextLinkInput { Text = x }).ToList(),
            Shifts = Texts(issues, ProductionScheduleImportIssueKind.UnknownShift).Select(x => new TextLinkInput { Text = x }).ToList(),
            Products = Texts(issues, ProductionScheduleImportIssueKind.UnknownSku).Select(x => new ProductLinkInput { Text = x }).ToList(),
            ProductLabels = resolutions.Products.Where(x => skus.ContainsKey(x.Value))
                .ToDictionary(x => x.Key, x => skus[x.Value], StringComparer.Ordinal)
        };
        // Text-level blockers are answered by a link; rows only get a control when the value itself is wrong or blank.
        var blocks = issues.Where(x => x.Row.HasValue && x.Table.HasValue && (
                x.Kind is ProductionScheduleImportIssueKind.InvalidQuantity or ProductionScheduleImportIssueKind.InvalidDate ||
                (x.Kind is ProductionScheduleImportIssueKind.UnknownSku or ProductionScheduleImportIssueKind.UnknownArea or
                    ProductionScheduleImportIssueKind.UnknownShift && string.IsNullOrWhiteSpace(x.Value))))
            .GroupBy(x => (x.Sheet, Table: x.Table!.Value, Row: x.Row!.Value))
            .Select(group => new RowBlock(Key(group.Key.Sheet, group.Key.Table, group.Key.Row), group.Key.Sheet, group.Key.Table,
                group.Key.Row, group.ToArray(),
                group.Any(x => x.Kind == ProductionScheduleImportIssueKind.InvalidQuantity),
                group.Any(x => x.Kind == ProductionScheduleImportIssueKind.InvalidDate)))
            .ToArray();
        RowBlocks = blocks.Take(MaxRowFixes).ToArray();
        HiddenRowBlocks = blocks.Length - RowBlocks.Count;
        Resolution.Rows = RowBlocks.Select(block =>
        {
            var fix = resolutions.Rows.LastOrDefault(x => x.Sheet == block.Sheet && x.Table == block.Table && x.Row == block.Row);
            return new RowFixInput { Key = block.Key, Quantity = fix?.Quantity, Date = fix?.Date };
        }).ToList();
        ProductionDailyText.LocalizeErrors(ModelState, texts);
        return Page();
    }

    private static IEnumerable<string> Texts(IEnumerable<ProductionScheduleImportIssue> issues, ProductionScheduleImportIssueKind kind) =>
        issues.Where(x => x.Kind == kind && !string.IsNullOrWhiteSpace(x.Value)).Select(x => x.Value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);

    private static ProductionScheduleImportResolutions Merge(ProductionScheduleImportResolutions current, ResolutionInput input)
    {
        var areas = new Dictionary<string, ProductionDailyArea>(current.Areas, StringComparer.Ordinal);
        foreach (var link in input.Areas)
            if (!string.IsNullOrWhiteSpace(link.Text) && Enum.TryParse<ProductionDailyArea>(link.Value, out var area) && Enum.IsDefined(area))
                areas[link.Text.Trim()] = area;
        var shifts = new Dictionary<string, int>(current.Shifts, StringComparer.Ordinal);
        foreach (var link in input.Shifts)
            if (!string.IsNullOrWhiteSpace(link.Text) && link.Value is "1" or "2")
                shifts[link.Text.Trim()] = link.Value == "1" ? 1 : 2;
        var products = new Dictionary<string, Guid>(current.Products, StringComparer.Ordinal);
        foreach (var link in input.Products)
            if (!string.IsNullOrWhiteSpace(link.Text) && link.ProductId is { } id && id != Guid.Empty)
                products[link.Text.Trim()] = id;
        var rows = current.Rows.ToDictionary(x => (x.Sheet, x.Table, x.Row));
        foreach (var fix in input.Rows)
        {
            if (!TryParseKey(fix.Key, out var sheet, out var table, out var row)) continue;
            if (fix.Skip) rows[(sheet, table, row)] = new(sheet, table, row, Skip: true);
            else if (fix.Quantity.HasValue || fix.Date.HasValue) rows[(sheet, table, row)] = new(sheet, table, row, false, fix.Quantity, fix.Date);
        }
        // Changes to source interpretation invalidate the previously accepted physical opening.
        return new(areas, shifts, products, rows.Values.ToArray()) { ClosingTable = current.ClosingTable };
    }

    // Sheet goes last because worksheet names may contain the separator.
    private static string Key(string sheet, ProductionScheduleImportTable table, int row) => $"{table}|{row}|{sheet}";
    private static bool TryParseKey(string? key, out string sheet, out ProductionScheduleImportTable table, out int row)
    {
        var parts = (key ?? "").Split('|', 3);
        sheet = parts.Length == 3 ? parts[2] : "";
        row = 0;
        table = default;
        return parts.Length == 3 && Enum.TryParse(parts[0], out table) && Enum.IsDefined(table) &&
               int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out row) && sheet.Length > 0;
    }

    private void AddErrors(ProductionDailyCommandResult result)
    {
        var errors = result.Errors is { Count: > 0 } ? result.Errors : [result.Status switch
        {
            ProductionDailyCommandStatus.ConcurrencyConflict => "Los datos cambiaron. Recarga antes de continuar.",
            ProductionDailyCommandStatus.IdempotencyConflict => "La operación ya se utilizó con otros datos.",
            _ => "No fue posible completar la operación."
        }];
        foreach (var error in errors) ModelState.AddModelError(string.Empty, error);
    }

    private Guid Actor() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id : throw new InvalidOperationException("La sesión ADMIN no tiene identificador.");

    public sealed record RowBlock(string Key, string Sheet, ProductionScheduleImportTable Table, int Row,
        IReadOnlyList<ProductionScheduleImportIssue> Issues, bool NeedsQuantity, bool NeedsDate);

    public sealed class ResolutionInput
    {
        public List<TextLinkInput> Areas { get; set; } = [];
        public List<TextLinkInput> Shifts { get; set; } = [];
        public List<ProductLinkInput> Products { get; set; } = [];
        public List<RowFixInput> Rows { get; set; } = [];
        internal Dictionary<string, string> ProductLabels { get; set; } = new(StringComparer.Ordinal);
    }
    public sealed class TextLinkInput { public string Text { get; set; } = ""; public string? Value { get; set; } }
    public sealed class ProductLinkInput { public string Text { get; set; } = ""; public Guid? ProductId { get; set; } public string? ProductLabel { get; set; } }
    public sealed class RowFixInput
    {
        public string Key { get; set; } = "";
        public bool Skip { get; set; }
        public decimal? Quantity { get; set; }
        public DateOnly? Date { get; set; }
    }
    public sealed class OpeningInput
    {
        public Guid ProductId { get; set; }
        public string? Cutting { get; set; }
        public string? Sewing { get; set; }
        public string? ReadyToPack { get; set; }
        public string? Reason { get; set; }
    }
}
