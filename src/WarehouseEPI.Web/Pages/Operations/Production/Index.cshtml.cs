using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Production;
using WarehouseEPI.Web.Localization;
using Microsoft.Extensions.Localization;

namespace WarehouseEPI.Web.Pages.Operations.Production;

public sealed partial class IndexModel(
    ProductionDailyScheduleService schedules,
    ProductionDailyCaptureService captures,
    ProductionDailyBalanceService balances,
    ProductionDailyExportService exports,
    WarehouseDbContext db,
    WarehouseClock clock,
    TimeProvider timeProvider, IStringLocalizer<ProductionTexts> texts) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid? WeekId { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Day { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sku { get; set; }
    [BindProperty(SupportsGet = true)] public ProductionDailyArea? Area { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? ShiftId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Reference { get; set; }
    [BindProperty] public CaptureInput Capture { get; set; } = new();
    [BindProperty] public ReverseInput Reverse { get; set; } = new();
    public IReadOnlyList<ProductionScheduleWeekView> Weeks { get; private set; } = [];
    public IReadOnlyList<ProductionShift> Shifts { get; private set; } = [];
    [BindProperty(SupportsGet = true)] public DateOnly? Through { get; set; }
    public ProductionDailySummary? Daily { get; private set; }
    private static DateOnly DefaultThrough(DateOnly start, DateOnly end, DateOnly today) => today < start || today > start.AddDays(5) ? start.AddDays(5) : today;
    public ProductionDailyBalanceView? Balance { get; private set; }
    public IReadOnlyList<ProductionDailyCaptureDetail> Details { get; private set; } = [];
    public ProductionDailyCapturePreview? Preview { get; private set; }
    public DateOnly Today { get; private set; }
    public bool ConfigurationReady { get; private set; }

    public async Task OnGetAsync(CancellationToken token) => await LoadAsync(token);

    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken token)
    {
        if (!await RequireConfigurationAsync(token)) { await LoadAsync(token, true); ProductionDailyText.LocalizeErrors(ModelState, texts); return Page(); }
        if (ProductionCapture.ValidateOnly(this, nameof(Capture)))
            Preview = await captures.PreviewAsync(new(Capture.EffectiveDate, Capture.Area, Capture.ShiftId,
                Capture.ProductId, Capture.Quantity), token);
        await LoadAsync(token, preserveCapture: true);
        ProductionDailyText.LocalizeErrors(ModelState, texts); return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken token)
    {
        if (!await RequireConfigurationAsync(token)) { await LoadAsync(token, true); ProductionDailyText.LocalizeErrors(ModelState, texts); return Page(); }
        if (!ProductionCapture.ValidateOnly(this, nameof(Capture)))
        {
            await LoadAsync(token, preserveCapture: true);
            ProductionDailyText.LocalizeErrors(ModelState, texts); return Page();
        }
        var result = await captures.ConfirmAsync(new(Capture.OperationId, Capture.EffectiveDate, Capture.Area,
            Capture.ShiftId, Capture.ProductId, Capture.Quantity, Capture.Notes, Capture.Pin), token);
        Capture.Pin = string.Empty;
        ProductionCapture.ClearPins(this);
        if (result.Success)
        {
            TempData["Success"] = texts["Producción registrada; el balance y el siguiente proceso se actualizaron."].Value;
            return RedirectToPage(new { WeekId });
        }
        ModelState.AddModelError(string.Empty, result.Errors?.FirstOrDefault() ?? "No fue posible registrar la captura.");
        Preview = await captures.PreviewAsync(new(Capture.EffectiveDate, Capture.Area, Capture.ShiftId,
            Capture.ProductId, Capture.Quantity), token);
        await LoadAsync(token, preserveCapture: true);
        ProductionDailyText.LocalizeErrors(ModelState, texts); return Page();
    }

    public async Task<IActionResult> OnPostReverseAsync(CancellationToken token)
    {
        Tab = "history";
        if (!User.IsInRole("ADMIN")) return Forbid();
        if (!ProductionCapture.ValidateOnly(this, nameof(Reverse)))
        {
            await LoadAsync(token, preserveCapture: true);
            ProductionDailyText.LocalizeErrors(ModelState, texts); return Page();
        }
        var result = await captures.ReverseAsync(new(Reverse.OperationId, Reverse.CaptureId,
            Reverse.Reason ?? string.Empty, Reverse.Pin), token);
        Reverse.Pin = string.Empty;
        ProductionCapture.ClearPins(this);
        if (result.Success)
        {
            TempData["Success"] = texts["Captura revertida con trazabilidad. Registra una nueva captura si corresponde."].Value;
            return RedirectToPage(new { WeekId, Tab });
        }
        ModelState.AddModelError(string.Empty, result.Errors?.FirstOrDefault() ?? "No fue posible revertir la captura.");
        await LoadAsync(token, preserveCapture: true);
        ProductionDailyText.LocalizeErrors(ModelState, texts); return Page();
    }

    public async Task<IActionResult> OnGetExportAsync(Guid weekId, CancellationToken token)
    {
        var week = await schedules.GetWeekAsync(weekId, token);
        if (week is null) return NotFound();
        var today = await clock.GetDateAsync(timeProvider.GetUtcNow(), token);
        var filter = new ProductionWeeklyFilter(Through ?? DefaultThrough(week.WeekStart, week.WeekEnd, today), Sku, Reference, Area);
        var bytes = await exports.ExportAsync(weekId, filter, token);
        if (bytes is null) return NotFound();
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"Production Schedule {week?.WeekStart:yyyy-MM-dd}.xlsx");
    }

    private async Task LoadAsync(CancellationToken token, bool preserveCapture = false)
    {
        Today = await clock.GetDateAsync(timeProvider.GetUtcNow(), token);
        Weeks = await schedules.ListWeeksAsync(token);
        WeekId ??= ProductionDailySetup.DefaultWeek(Weeks, Today);
        if (Tab == "capture" && Day.HasValue && HttpMethods.IsGet(Request.Method))
            WeekId = Weeks.FirstOrDefault(x => x.WeekStart <= Day.Value && x.WeekEnd >= Day.Value)?.Id;
        if (WeekId is Guid id)
        {
            var balance = await balances.GetAsync(id, token);
            if (balance is null) ModelState.AddModelError(string.Empty,
                texts["La semana seleccionada ya no existe. Selecciona otra semana."]);
            if (balance is not null)
            {
                if (Tab == "balance")
                {
                    Daily = await balances.GetDailySummaryAsync(id, new(Through ?? DefaultThrough(balance.WeekStart, balance.WeekEnd, Today), Sku, Reference, Area), token);
                    Through = Daily?.Through;
                    ModelState.Remove(nameof(Through));
                }
                var rows = balance.Rows.AsEnumerable();
                if (Day.HasValue) rows = rows.Where(x => x.Date == Day);
                if (!string.IsNullOrWhiteSpace(Sku)) rows = rows.Where(x => x.Sku.Contains(Sku.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    (x.Description?.Contains(Sku.Trim(), StringComparison.OrdinalIgnoreCase) ?? false));
                if (!string.IsNullOrWhiteSpace(Reference)) rows = rows.Where(x => x.References.Any(r => r.Contains(Reference.Trim(), StringComparison.OrdinalIgnoreCase)));
                if (Area.HasValue) rows = rows.Where(x => Area.Value switch
                {
                    ProductionDailyArea.Cutting => x.Cutting.Applies,
                    ProductionDailyArea.Sewing => x.Sewing.Applies,
                    _ => x.ReadyToPack.Applies
                });
                Balance = balance with { Rows = rows.ToArray() };
            }
            Details = await balances.GetCaptureDetailsAsync(id, Day, area: Area, shiftId: ShiftId, token: token);
            if (!string.IsNullOrWhiteSpace(Sku)) Details = Details.Where(x => x.Sku.Contains(Sku.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        var setup = await ProductionDailySetup.LoadAsync(schedules, db, token);
        ConfigurationReady = setup.IsReady;
        Shifts = setup.CaptureShifts;
        await LoadGroupAsync(token);
        if (!preserveCapture)
        {
            Capture.OperationId = Guid.NewGuid();
            Capture.EffectiveDate = Today;
            Capture.ShiftId = Shifts.FirstOrDefault()?.Id ?? Guid.Empty;
        }
    }

    private async Task<bool> RequireConfigurationAsync(CancellationToken token)
    {
        var setup = await ProductionDailySetup.LoadAsync(schedules, db, token);
        if (setup.IsReady) return true;
        ModelState.AddModelError(string.Empty, texts["Completa la configuración de áreas y turnos activos antes de capturar."]);
        Capture.Pin = string.Empty;
        ProductionCapture.ClearPins(this);
        return false;
    }

    public sealed class CaptureInput
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        [Required(ErrorMessage = "Este campo es obligatorio.")] public DateOnly EffectiveDate { get; set; }
        [Required(ErrorMessage = "Este campo es obligatorio.")] public ProductionDailyArea Area { get; set; }
        [Required(ErrorMessage = "Este campo es obligatorio.")] public Guid ShiftId { get; set; }
        [Required(ErrorMessage = "Este campo es obligatorio.")] public Guid ProductId { get; set; }
        public string? ProductLabel { get; set; }
        [Range(typeof(decimal), "0.0001", "99999999999999", ErrorMessage = "Indica una cantidad positiva con hasta cuatro decimales."), ProductionQuantity]
        public decimal Quantity { get; set; }
        [StringLength(500, ErrorMessage = "Usa como máximo {1} caracteres.")] public string? Notes { get; set; }
        [Required(ErrorMessage = "Este campo es obligatorio."), RegularExpression("^[0-9]{4,8}$", ErrorMessage = "Usa un NIP de 4 a 8 dígitos.")] public string Pin { get; set; } = string.Empty;
    }
    public sealed class ReverseInput
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        public Guid CaptureId { get; set; }
        [Required(ErrorMessage = "Este campo es obligatorio."), StringLength(500, ErrorMessage = "Usa como máximo {1} caracteres.")] public string? Reason { get; set; }
        [Required(ErrorMessage = "Este campo es obligatorio."), RegularExpression("^[0-9]{4,8}$", ErrorMessage = "Usa un NIP de 4 a 8 dígitos.")] public string Pin { get; set; } = string.Empty;
    }
}
