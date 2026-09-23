using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Web.Pages.Operations.Production;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Production;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Admin.Production;

public sealed partial class ScheduleModel(ProductionDailyScheduleService service, WarehouseDbContext db,
    WarehouseClock clock, TimeProvider timeProvider, IStringLocalizer<ProductionTexts> texts) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid? WeekId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? EditLineId { get; set; }
    [BindProperty] public CreateWeekInput NewWeek { get; set; } = new();
    [BindProperty] public LineInput Line { get; set; } = new();
    [BindProperty] public PublishInput Publish { get; set; } = new();
    [BindProperty] public ConfigurationInput Configuration { get; set; } = new();
    public IReadOnlyList<ProductionScheduleWeekView> Weeks { get; private set; } = [];
    public ProductionScheduleWeekView? Week { get; private set; }
    public ProductionDailyConfigurationView DailyConfiguration { get; private set; } = null!;
    public IReadOnlyList<ProductionStage> Stages { get; private set; } = [];
    public IReadOnlyList<ProductionShift> Shifts { get; private set; } = [];
    public bool ConfigurationReady { get; private set; }

    public async Task OnGetAsync(CancellationToken token) => await LoadAsync(token, initialize: true);

    public async Task<IActionResult> OnPostCreateWeekAsync(CancellationToken token)
    {
        if (NewWeek.WeekStart == DateOnly.MinValue)
            ModelState.AddModelError("NewWeek.WeekStart", texts["Selecciona una fecha válida."]);
        if (!ProductionCapture.ValidateOnly(this, nameof(NewWeek))) { await LoadAsync(token); ProductionDailyText.LocalizeErrors(ModelState, texts); return Page(); }
        var result = await service.CreateWeekAsync(new(NewWeek.OperationId, NewWeek.WeekStart, Actor()), token);
        return await FinishAsync(result, "Semana creada en borrador.", result.Id, token);
    }

    public async Task<IActionResult> OnPostSaveLineAsync(CancellationToken token)
    {
        WeekId = Line.WeekId;
        if (!ProductionCapture.ValidateOnly(this, nameof(Line))) { await LoadAsync(token); ProductionDailyText.LocalizeErrors(ModelState, texts); return Page(); }
        var result = await service.SaveLineAsync(new(Line.OperationId, Line.WeekId, Line.LineId,
            Line.ExpectedWeekVersion, Line.ExpectedLineVersion, Line.PlannedDate, Line.ProductId,
            Line.Quantity, Line.OrderReference1, Line.OrderReference2, Line.OrderReference3, Line.Notes, Actor(), Line.Pin), token);
        Line.Pin = string.Empty; ProductionCapture.ClearPins(this);
        return await FinishAsync(result, "Línea guardada con revisión automática.", Line.WeekId, token);
    }

    public async Task<IActionResult> OnPostPublishAsync(CancellationToken token)
    {
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
            return RedirectToPage(new { WeekId = weekId, SelectedDay = Line.PlannedDate == DateOnly.MinValue ? null : (DateOnly?)Line.PlannedDate });
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
            var selected = EditLineId.HasValue ? Week.Lines.SingleOrDefault(x => x.Id == EditLineId) : null;
            if (initialize || handler != "SaveLine") Line = new()
            {
                WeekId = Week.Id, ExpectedWeekVersion = Week.Version, LineId = selected?.Id,
                ExpectedLineVersion = selected?.Version, PlannedDate = selected?.PlannedDate ?? (SelectedDay >= Week.WeekStart && SelectedDay <= Week.WeekEnd ? SelectedDay.Value : Week.WeekStart),
                ProductId = selected?.ProductId ?? Guid.Empty,
                ProductLabel = selected is null ? null : $"{selected.Sku} · {selected.Description}",
                Quantity = selected?.Quantity ?? 0, OrderReference1 = selected?.OrderReference1,
                OrderReference2 = selected?.OrderReference2, OrderReference3 = selected?.OrderReference3,
                Notes = selected?.Notes
            };
        }
    }

    private Guid Actor() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id : throw new InvalidOperationException("La sesión ADMIN no tiene identificador.");

    public sealed class CreateWeekInput { public Guid OperationId { get; set; } = Guid.NewGuid(); [Required(ErrorMessage = "Este campo es obligatorio.")] public DateOnly WeekStart { get; set; } }
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

