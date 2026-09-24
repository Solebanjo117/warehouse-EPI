using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Web.Pages.Operations.Production;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Production;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Admin.Production;

[RequestFormLimits(ValueCountLimit = 1600)]
public sealed partial class ScheduleModel(ProductionDailyScheduleService service, WarehouseDbContext db,
    WarehouseClock clock, TimeProvider timeProvider, IStringLocalizer<ProductionTexts> texts,
    OperationalInventoryQueryService productsQuery) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid? WeekId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? EditLineId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? DeleteLineId { get; set; }
    [BindProperty(SupportsGet = true)] public string View { get; set; } = "program";
    [BindProperty(SupportsGet = true)] public string? ActionPanel { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public bool AddLine { get; set; }
    [BindProperty] public CreateWeekInput NewWeek { get; set; } = new();
    [BindProperty] public LineInput Line { get; set; } = new();
    [BindProperty] public BatchInput Batch { get; set; } = new();
    [BindProperty] public DeleteLineInput DeleteLine { get; set; } = new();
    [BindProperty] public PublishInput Publish { get; set; } = new();
    [BindProperty] public ConfigurationInput Configuration { get; set; } = new();
    public IReadOnlyList<ProductionScheduleWeekView> Weeks { get; private set; } = [];
    public ProductionScheduleWeekView? Week { get; private set; }
    public ProductionDailyConfigurationView DailyConfiguration { get; private set; } = null!;
    public IReadOnlyList<ProductionStage> Stages { get; private set; } = [];
    public IReadOnlyList<ProductionShift> Shifts { get; private set; } = [];
    public bool ConfigurationReady { get; private set; }
    public IReadOnlyList<ProductionScheduleLineView> DayLines { get; private set; } = [];
    public IReadOnlyList<ProductionScheduleLineView> WeekLines { get; private set; } = [];
    public IReadOnlyDictionary<Guid, StagedProductView> StagedProducts { get; private set; } =
        new Dictionary<Guid, StagedProductView>();
    public int WeekLineCount { get; private set; }
    public int WeekSkuCount { get; private set; }
    public int WeekPageCount { get; private set; }
    public IReadOnlyDictionary<Guid, ProductionScheduleLineDeletion> DeletionEligibility { get; private set; } =
        new Dictionary<Guid, ProductionScheduleLineDeletion>();
    public int DayPageCount { get; private set; }
    public IReadOnlyList<ProductionSchedulePlanSummaryRow> PlanSummary { get; private set; } = [];
    public IReadOnlyList<string> PublicationIssues { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken token) => await LoadAsync(token, initialize: true);

    public async Task<IActionResult> OnPostCreateWeekAsync(CancellationToken token)
    {
        ActionPanel = "new";
        if (NewWeek.WeekStart == DateOnly.MinValue)
            ModelState.AddModelError("NewWeek.WeekStart", texts["Selecciona una fecha válida."]);
        var existing = await db.ProductionScheduleWeeks.AsNoTracking().Where(x => x.WeekStart == NewWeek.WeekStart)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token);
        if (existing.HasValue) return RedirectToPage(new { WeekId = existing.Value, SelectedDay = IsoDay(NewWeek.WeekStart) });
        if (!ProductionCapture.ValidateOnly(this, nameof(NewWeek))) { await LoadAsync(token); ProductionDailyText.LocalizeErrors(ModelState, texts); return Page(); }
        var result = await service.CreateWeekAsync(new(NewWeek.OperationId, NewWeek.WeekStart, Actor()), token);
        return await FinishAsync(result, "Semana creada en borrador.", result.Id, token);
    }

    public async Task<IActionResult> OnPostSaveLineAsync(CancellationToken token)
    {
        View = "program";
        WeekId = Line.WeekId;
        SelectedDay = Line.PlannedDate;
        AddLine = true;
        EditLineId = Line.LineId;
        if (!ProductionCapture.ValidateOnly(this, nameof(Line))) { await LoadAsync(token); ProductionDailyText.LocalizeErrors(ModelState, texts); return Page(); }
        var result = await service.SaveLineAsync(new(Line.OperationId, Line.WeekId, Line.LineId,
            Line.ExpectedWeekVersion, Line.ExpectedLineVersion, Line.PlannedDate, Line.ProductId,
            Line.Quantity, Line.OrderReference1, Line.OrderReference2, Line.OrderReference3, Line.Notes, Actor(), Line.Pin,
            Line.OriginalType, Line.OriginalAnnotation1, Line.OriginalAnnotation2,
            Line.OriginalAnnotation1Kind, Line.OriginalAnnotation2Kind), token);
        Line.Pin = string.Empty; ProductionCapture.ClearPins(this);
        return await FinishAsync(result, "Línea guardada con revisión automática.", Line.WeekId, token);
    }

    public async Task<IActionResult> OnPostStageLineAsync(CancellationToken token)
    {
        View = "program";
        WeekId = Batch.WeekId;
        AddLine = true;
        SelectedDay = Line.PlannedDate;
        var lineValid = ProductionCapture.ValidateOnly(this, nameof(Line));
        if (Batch.Rows.Count >= 100)
            ModelState.AddModelError(string.Empty, "Confirma este grupo antes de agregar más de 100 renglones.");
        var week = await db.ProductionScheduleWeeks.AsNoTracking().Where(x => x.Id == Batch.WeekId)
            .Select(x => new { x.WeekStart, x.WeekEnd, x.Status }).SingleOrDefaultAsync(token);
        if (week is null || week.Status == ProductionScheduleWeekStatus.Closed)
            ModelState.AddModelError(string.Empty, "La semana ya no admite renglones nuevos.");
        else if (Line.PlannedDate < week.WeekStart || Line.PlannedDate > week.WeekEnd)
            ModelState.AddModelError(nameof(Line.PlannedDate), "La fecha debe pertenecer a la semana seleccionada.");
        var product = await db.Products.AsNoTracking().Where(x => x.Id == Line.ProductId && x.IsActive)
            .Select(x => new { x.BaseUnit.AllowsDecimals }).SingleOrDefaultAsync(token);
        if (product is null)
            ModelState.AddModelError(nameof(Line.ProductId), "Selecciona un SKU activo del catálogo.");
        else if (!product.AllowsDecimals && decimal.Truncate(Line.Quantity) != Line.Quantity)
            ModelState.AddModelError(nameof(Line.Quantity), "La unidad del SKU no permite decimales.");
        if (!lineValid || !ModelState.IsValid)
        {
            await LoadAsync(token);
            ProductionDailyText.LocalizeErrors(ModelState, texts);
            return Page();
        }
        Batch.Rows.Add(new()
        {
            PlannedDate = Line.PlannedDate, ProductId = Line.ProductId, Quantity = Line.Quantity,
            OrderReference1 = Line.OrderReference1, OrderReference2 = Line.OrderReference2,
            OrderReference3 = Line.OrderReference3, Notes = Line.Notes, OriginalType = Line.OriginalType,
            OriginalAnnotation1 = Line.OriginalAnnotation1, OriginalAnnotation2 = Line.OriginalAnnotation2,
            OriginalAnnotation1Kind = Line.OriginalAnnotation1Kind,
            OriginalAnnotation2Kind = Line.OriginalAnnotation2Kind
        });
        var day = Line.PlannedDate;
        Line = new() { WeekId = Batch.WeekId, PlannedDate = day };
        ModelState.Clear();
        await LoadAsync(token);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveStagedLineAsync(int index, CancellationToken token)
    {
        View = "program";
        WeekId = Batch.WeekId;
        AddLine = true;
        if (index >= 0 && index < Batch.Rows.Count) Batch.Rows.RemoveAt(index);
        ModelState.Clear();
        await LoadAsync(token);
        return Page();
    }

    public async Task<IActionResult> OnPostEditStagedLineAsync(int index, CancellationToken token)
    {
        View = "program";
        WeekId = Batch.WeekId;
        AddLine = true;
        if (index >= 0 && index < Batch.Rows.Count)
        {
            var row = Batch.Rows[index];
            Batch.Rows.RemoveAt(index);
            SelectedDay = row.PlannedDate;
            Line = new()
            {
                WeekId = Batch.WeekId, ExpectedWeekVersion = Batch.ExpectedWeekVersion,
                PlannedDate = row.PlannedDate, ProductId = row.ProductId, Quantity = row.Quantity,
                ProductLabel = await db.Products.AsNoTracking().Where(x => x.Id == row.ProductId)
                    .Select(x => x.Sku).SingleOrDefaultAsync(token),
                OrderReference1 = row.OrderReference1, OrderReference2 = row.OrderReference2,
                OrderReference3 = row.OrderReference3, Notes = row.Notes,
                OriginalType = row.OriginalType, OriginalAnnotation1 = row.OriginalAnnotation1,
                OriginalAnnotation2 = row.OriginalAnnotation2,
                OriginalAnnotation1Kind = row.OriginalAnnotation1Kind,
                OriginalAnnotation2Kind = row.OriginalAnnotation2Kind
            };
        }
        ModelState.Clear();
        await LoadAsync(token);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveBatchAsync(CancellationToken token)
    {
        View = "program";
        WeekId = Batch.WeekId;
        AddLine = true;
        var result = await service.SaveBatchAsync(new(Batch.OperationId, Batch.WeekId,
            Batch.ExpectedWeekVersion, Batch.Rows.Select(x => new ProductionScheduleBatchLine(
                x.PlannedDate, x.ProductId, x.Quantity, x.OrderReference1, x.OrderReference2,
                x.OrderReference3, x.Notes, x.OriginalType, x.OriginalAnnotation1,
                x.OriginalAnnotation2, x.OriginalAnnotation1Kind, x.OriginalAnnotation2Kind)).ToArray(),
            Actor(), Batch.Pin ?? string.Empty), token);
        Batch.Pin = null;
        ProductionCapture.ClearPins(this);
        if (result.Success)
        {
            TempData["Success"] = $"{Batch.Rows.Count} renglones guardados en el programa semanal.";
            return RedirectToPage(new { WeekId, SelectedDay = IsoDay(SelectedDay) });
        }
        foreach (var error in result.Errors is { Count: > 0 } ? result.Errors :
                     [result.Status == ProductionDailyCommandStatus.ConcurrencyConflict
                         ? "El programa cambió. Revisa la semana y vuelve a confirmar el grupo."
                         : "No se guardó ningún renglón. Revisa el grupo y vuelve a intentar."])
            ModelState.AddModelError(string.Empty, ProductionDailyText.Message(texts, error));
        await LoadAsync(token);
        return Page();
    }

    public async Task<IActionResult> OnPostCancelLineAsync(CancellationToken token)
    {
        View = "program";
        WeekId = DeleteLine.WeekId;
        DeleteLineId = DeleteLine.LineId;
        var result = await service.CancelLineAsync(new(DeleteLine.OperationId, DeleteLine.WeekId,
            DeleteLine.LineId, DeleteLine.ExpectedWeekVersion, DeleteLine.ExpectedLineVersion,
            Actor(), DeleteLine.Pin ?? string.Empty), token);
        DeleteLine.Pin = null;
        ProductionCapture.ClearPins(this);
        if (result.Success)
        {
            TempData["Success"] = "Renglón eliminado del programa semanal.";
            return RedirectToPage(new { WeekId, SelectedDay = IsoDay(SelectedDay), PageNumber });
        }
        foreach (var error in result.Errors is { Count: > 0 } ? result.Errors :
                     [result.Status == ProductionDailyCommandStatus.ConcurrencyConflict
                         ? "El programa cambió. Recarga el día antes de eliminar."
                         : "No fue posible eliminar el renglón."])
            ModelState.AddModelError(string.Empty, ProductionDailyText.Message(texts, error));
        await LoadAsync(token);
        return Page();
    }

    public async Task<IActionResult> OnPostPublishAsync(CancellationToken token)
    {
        View = "review";
        WeekId = Publish.WeekId;
        if (!ProductionCapture.ValidateOnly(this, nameof(Publish))) { await LoadAsync(token); ProductionDailyText.LocalizeErrors(ModelState, texts); return Page(); }
        var result = await service.PublishAsync(new(Publish.OperationId, Publish.WeekId,
            Publish.ExpectedVersion, Publish.Pin, Actor()), token);
        Publish.Pin = string.Empty;
        ProductionCapture.ClearPins(this);
        return await FinishAsync(result, "Semana abierta para capturar.", Publish.WeekId, token);
    }

    public async Task<IActionResult> OnPostCloseAsync(Guid weekId, uint version, CancellationToken token)
    {
        var result = await service.CloseAsync(new(Guid.NewGuid(), weekId, version, Actor()), token);
        return await FinishAsync(result, "Semana cerrada.", weekId, token);
    }

    public async Task<IActionResult> OnPostReopenAsync(Guid weekId, uint version, CancellationToken token)
    {
        var result = await service.ReopenAsync(new(Guid.NewGuid(), weekId, version, Actor()), token);
        return await FinishAsync(result, "Semana reabierta con auditoría.", weekId, token);
    }

    public async Task<IActionResult> OnPostConfigureAsync(CancellationToken token)
    {
        ActionPanel = "config";
        if (!ProductionCapture.ValidateOnly(this, nameof(Configuration))) { await LoadAsync(token); ProductionDailyText.LocalizeErrors(ModelState, texts); return Page(); }
        var result = await service.ConfigureAsync(new(Configuration.OperationId, Configuration.ExpectedVersion,
            Configuration.CuttingStageId, Configuration.SewingStageId, Configuration.ReadyToPackStageId,
            Configuration.Shift1Id, Configuration.Shift2Id, Actor()), token);
        return await FinishAsync(result, "Áreas y turnos del programa diario actualizados.", WeekId, token);
    }

    private async Task<IActionResult> FinishAsync(ProductionDailyCommandResult result, string success,
        Guid? weekId, CancellationToken token)
    {
        if (result.Success)
        {
            TempData["Success"] = texts[success].Value;
            return RedirectToPage(new { WeekId = weekId, SelectedDay = IsoDay(SelectedDay ?? (Line.PlannedDate == DateOnly.MinValue ? null : (DateOnly?)Line.PlannedDate)) });
        }
        var errors = result.Errors is { Count: > 0 } ? result.Errors : [result.Status switch
        {
            ProductionDailyCommandStatus.ConcurrencyConflict => "Los datos cambiaron. Recarga antes de continuar.",
            ProductionDailyCommandStatus.InvalidPin => "NIP ADMIN inválido.",
            ProductionDailyCommandStatus.IdempotencyConflict => "La operación ya se utilizó con otros datos.",
            _ => "No fue posible completar la operación."
        }];
        foreach (var error in errors) ModelState.AddModelError(string.Empty, ProductionDailyText.Message(texts, error));
        WeekId = weekId ?? WeekId;
        await LoadAsync(token);
        ProductionDailyText.LocalizeErrors(ModelState, texts); return Page();
    }

    private async Task LoadAsync(CancellationToken token, bool initialize = false)
    {
        Weeks = await service.ListWeeksAsync(token);
        var today = await clock.GetDateAsync(timeProvider.GetUtcNow(), token);
        WeekId ??= ProductionDailySetup.DefaultWeek(Weeks, today);
        Week = WeekId.HasValue ? await service.GetWeekAsync(WeekId.Value, token) : null;
        if (WeekId.HasValue && Week is null)
            ModelState.AddModelError(string.Empty, texts["La semana seleccionada ya no existe. Selecciona otra semana."]);
        var setup = await ProductionDailySetup.LoadAsync(service, db, token);
        DailyConfiguration = setup.Configuration;
        ConfigurationReady = setup.IsReady;
        Stages = setup.Stages;
        Shifts = setup.Shifts;
        var handler = RouteData.Values["handler"]?.ToString() ?? Request.Query["handler"].ToString();
        if (View is not ("program" or "week" or "summary" or "review")) View = "program";
        if (Week is not null)
        {
            SelectedDay = SelectedDay >= Week.WeekStart && SelectedDay <= Week.WeekEnd
                ? SelectedDay : today >= Week.WeekStart && today <= Week.WeekEnd ? today : Week.WeekStart;
            var selectedLines = Week.Lines.Where(x => x.PlannedDate == SelectedDay).OrderBy(x => x.Sequence).ToArray();
            DayPageCount = Math.Max(1, (selectedLines.Length + 24) / 25);
            if (View != "week") PageNumber = Math.Clamp(PageNumber, 1, DayPageCount);
            DayLines = selectedLines.Skip((Math.Clamp(PageNumber, 1, DayPageCount) - 1) * 25).Take(25).ToArray();
            var weeklyLines = Week.Lines.Where(x => !x.IsCarryover)
                .OrderBy(x => x.PlannedDate).ThenBy(x => x.Sequence).ToArray();
            WeekLineCount = weeklyLines.Length;
            WeekSkuCount = weeklyLines.Select(x => x.ProductId).Distinct().Count();
            WeekPageCount = Math.Max(1, (WeekLineCount + 24) / 25);
            if (View == "week")
            {
                PageNumber = Math.Clamp(PageNumber, 1, WeekPageCount);
                WeekLines = weeklyLines.Skip((PageNumber - 1) * 25).Take(25).ToArray();
            }
            DeletionEligibility = await service.GetDeletionEligibilityAsync(Week.Id,
                DayLines.Select(x => x.Id).ToArray(), token);
            PlanSummary = await service.GetPlanSummaryAsync(Week.Id, token);
            if (View == "review") PublicationIssues = await service.GetPublicationIssuesAsync(Week.Id, token);
            if (initialize || handler is not ("StageLine" or "RemoveStagedLine" or "EditStagedLine" or "SaveBatch"))
                Batch = new() { WeekId = Week.Id, ExpectedWeekVersion = Week.Version };
            if (Batch.Rows.Count > 0)
            {
                var ids = Batch.Rows.Select(x => x.ProductId).Distinct().ToArray();
                var products = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id))
                    .Select(x => new { x.Id, x.Sku, Unit = x.BaseUnit.Code }).ToArrayAsync(token);
                StagedProducts = products.ToDictionary(x => x.Id, x => new StagedProductView(x.Sku, x.Unit));
            }
        }
        await LoadWorkflowAsync(handler, token);
        if (initialize || handler != "CreateWeek") NewWeek.WeekStart = ProductionDailySetup.Monday(today);
        if (initialize || handler != "Configure")
        Configuration = new()
        {
            ExpectedVersion = DailyConfiguration.Version,
            CuttingStageId = setup.SuggestStage(DailyConfiguration.CuttingStageId, "CUT", "CUTTING", "CORTE"),
            SewingStageId = setup.SuggestStage(DailyConfiguration.SewingStageId, "SEW", "SEWING", "COSTURA"),
            ReadyToPackStageId = setup.SuggestStage(DailyConfiguration.ReadyToPackStageId, "RTP", "READY TO PACK", "LISTO PARA EMPACAR"),
            Shift1Id = setup.SuggestShift(DailyConfiguration.Shift1Id, "T1", "SHIFT 1", "TURNO 1"),
            Shift2Id = setup.SuggestShift(DailyConfiguration.Shift2Id, "T2", "SHIFT 2", "TURNO 2")
        };
        if (Week is not null)
        {
            if (initialize || handler != "Publish")
            {
                Publish.WeekId = Week.Id;
                Publish.ExpectedVersion = Week.Version;
            }
            if (initialize || handler != "CancelLine")
            {
                var deleting = DeleteLineId.HasValue ? Week.Lines.SingleOrDefault(x => x.Id == DeleteLineId) : null;
                DeleteLine = new()
                {
                    WeekId = Week.Id, LineId = deleting?.Id ?? Guid.Empty,
                    ExpectedWeekVersion = Week.Version, ExpectedLineVersion = deleting?.Version ?? 0
                };
            }
            var selected = EditLineId.HasValue ? Week.Lines.SingleOrDefault(x => x.Id == EditLineId) : null;
            if (initialize || handler is not ("SaveLine" or "StageLine" or "EditStagedLine")) Line = new()
            {
                WeekId = Week.Id, ExpectedWeekVersion = Week.Version, LineId = selected?.Id,
                ExpectedLineVersion = selected?.Version, PlannedDate = selected?.PlannedDate ?? (SelectedDay >= Week.WeekStart && SelectedDay <= Week.WeekEnd ? SelectedDay.Value : Week.WeekStart),
                ProductId = selected?.ProductId ?? Guid.Empty,
                ProductLabel = selected?.Sku,
                Quantity = selected?.Quantity ?? 0, OrderReference1 = selected?.OrderReference1,
                OrderReference2 = selected?.OrderReference2, OrderReference3 = selected?.OrderReference3,
                Notes = selected?.Notes, OriginalType = selected?.OriginalType,
                OriginalAnnotation1 = selected?.OriginalAnnotation1,
                OriginalAnnotation2 = selected?.OriginalAnnotation2,
                OriginalAnnotation1Kind = selected?.OriginalAnnotation1Kind,
                OriginalAnnotation2Kind = selected?.OriginalAnnotation2Kind
            };
        }
    }

    private Guid Actor() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id : throw new InvalidOperationException("La sesión ADMIN no tiene identificador.");

    private static string? IsoDay(DateOnly? day) => day?.ToString("yyyy-MM-dd", global::System.Globalization.CultureInfo.InvariantCulture);

    public sealed class CreateWeekInput { public Guid OperationId { get; set; } = Guid.NewGuid(); [Required(ErrorMessage = "Este campo es obligatorio.")] public DateOnly WeekStart { get; set; } }
    public sealed class DeleteLineInput
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        public Guid WeekId { get; set; }
        public Guid LineId { get; set; }
        public uint ExpectedWeekVersion { get; set; }
        public uint ExpectedLineVersion { get; set; }
        public string? Pin { get; set; }
    }
    public sealed class BatchInput
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        public Guid WeekId { get; set; }
        public uint ExpectedWeekVersion { get; set; }
        public List<BatchLineInput> Rows { get; set; } = [];
        public string? Pin { get; set; }
    }
    public sealed class BatchLineInput
    {
        public DateOnly PlannedDate { get; set; }
        public Guid ProductId { get; set; }
        public decimal Quantity { get; set; }
        public string? OrderReference1 { get; set; }
        public string? OrderReference2 { get; set; }
        public string? OrderReference3 { get; set; }
        public string? Notes { get; set; }
        public string? OriginalType { get; set; }
        public string? OriginalAnnotation1 { get; set; }
        public string? OriginalAnnotation2 { get; set; }
        public string? OriginalAnnotation1Kind { get; set; }
        public string? OriginalAnnotation2Kind { get; set; }
    }
    public sealed record StagedProductView(string Sku, string Unit);
    [BindProperty(SupportsGet = true)] public DateOnly? SelectedDay { get; set; }
    public sealed class LineInput
    {
        public Guid OperationId { get; set; } = Guid.NewGuid(); public Guid WeekId { get; set; }
        public Guid? LineId { get; set; } public uint ExpectedWeekVersion { get; set; } public uint? ExpectedLineVersion { get; set; }
        [Required(ErrorMessage = "Este campo es obligatorio.")] public DateOnly PlannedDate { get; set; } [Required(ErrorMessage = "Este campo es obligatorio.")] public Guid ProductId { get; set; }
        public string? ProductLabel { get; set; }
        public string Pin { get; set; } = string.Empty;
        [Range(typeof(decimal), "0.0001", "99999999999999", ErrorMessage = "Indica una cantidad positiva con hasta cuatro decimales."), ProductionQuantity] public decimal Quantity { get; set; }
        [StringLength(120, ErrorMessage = "Usa como máximo {1} caracteres.")] public string? OrderReference1 { get; set; } [StringLength(120, ErrorMessage = "Usa como máximo {1} caracteres.")] public string? OrderReference2 { get; set; }
        [StringLength(120, ErrorMessage = "Usa como máximo {1} caracteres.")] public string? OrderReference3 { get; set; } [StringLength(500, ErrorMessage = "Usa como máximo {1} caracteres.")] public string? Notes { get; set; }
        [StringLength(120)] public string? OriginalType { get; set; }
        [StringLength(500)] public string? OriginalAnnotation1 { get; set; }
        [StringLength(500)] public string? OriginalAnnotation2 { get; set; }
        public string? OriginalAnnotation1Kind { get; set; }
        public string? OriginalAnnotation2Kind { get; set; }
    }
    public sealed class PublishInput
    {
        public Guid OperationId { get; set; } = Guid.NewGuid(); public Guid WeekId { get; set; }
        public uint ExpectedVersion { get; set; } [Required(ErrorMessage = "Este campo es obligatorio."), RegularExpression("^[0-9]{4,8}$", ErrorMessage = "Usa un NIP de 4 a 8 dígitos.")] public string Pin { get; set; } = string.Empty;
    }
    public sealed class ConfigurationInput
    {
        public Guid OperationId { get; set; } = Guid.NewGuid(); public uint ExpectedVersion { get; set; }
        public Guid CuttingStageId { get; set; } public Guid SewingStageId { get; set; } public Guid ReadyToPackStageId { get; set; }
        public Guid Shift1Id { get; set; } public Guid Shift2Id { get; set; }
    }
}

