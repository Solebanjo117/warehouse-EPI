using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Admin.Production;

[Authorize(Policy = "AdminOnly")]
public sealed class ProcessEditModel(ProductionProcessConfigurationService processes, ProductionWipDefaultService wipDefaults) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public ProcessEditView Process { get; private set; } = null!;
    public bool IsEdit => Input.Id != Guid.Empty;
    public IReadOnlyList<TargetSelection> SelectedTargets
    {
        get
        {
            var options = Process.Areas.Concat(Process.Rows).Concat(Process.Racks).ToDictionary(x => x.Key, StringComparer.Ordinal);
            return Input.Targets.Distinct(StringComparer.Ordinal).Select(key =>
            {
                var isRack = key.StartsWith("R:", StringComparison.Ordinal);
                var isRow = key.StartsWith("F:", StringComparison.Ordinal);
                return options.TryGetValue(key, out var option)
                    ? new TargetSelection(option.Key, option.Label, isRow ? "Fila completa" : isRack ? "Rack WIP" : "Área WIP",
                        isRow ? "Aplica a todos los racks de la fila, incluidos los de almacenamiento"
                        : isRack ? "Aplica a todas las posiciones del rack" : "Área de proceso WIP", option.IsActive)
                    : new TargetSelection(key, key, isRow ? "Fila completa" : isRack ? "Rack WIP" : "Área WIP",
                        "La asociación ya no está disponible", false);
            }).ToArray();
        }
    }
    public bool HasGroupedTargetChanges => !Process.Rows.Concat(Process.Racks).Where(x => x.Selected).Select(x => x.Key)
        .ToHashSet(StringComparer.Ordinal).SetEquals(Input.Targets.Where(x => x.StartsWith("F:", StringComparison.Ordinal)
            || x.StartsWith("R:", StringComparison.Ordinal)));

    public async Task<IActionResult> OnGetAsync(Guid? id, CancellationToken token)
    {
        var process = await processes.GetAsync(id ?? Guid.Empty, token); if (process is null) return NotFound();
        Process = process; Input = new() { Id = process.Id, Code = process.Code, Name = process.Name, IsActive = process.IsActive,
            ExpectedVersion = process.Version, Targets = process.Areas.Concat(process.Rows).Concat(process.Racks).Where(x => x.Selected).Select(x => x.Key).ToList(),
            DefaultWipTargetKey = process.DefaultWipTargetKey, InactivityAlertHours = process.InactivityAlertHours,
            ReworkAlertHours = process.ReworkAlertHours };
        return Page();
    }

    public async Task<IActionResult> OnGetWipTargetsAsync(string? q, CancellationToken token) =>
        new JsonResult(await processes.SearchWipTargetsAsync(q, token));
    public async Task<IActionResult> OnGetDefaultWipTargetsAsync(string? q, string[] targets, CancellationToken token) =>
        new JsonResult(await wipDefaults.SearchProposedAsync(targets, q, token));

    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        var areas = new List<Guid>(); var rows = new List<string>(); var racks = new List<WipRackKey>();
        foreach (var key in Input.Targets.Distinct())
        {
            if (key.StartsWith("A:", StringComparison.Ordinal) && Guid.TryParse(key[2..], out var area)) areas.Add(area);
            else if (key.StartsWith("F:", StringComparison.Ordinal) && key.Length > 2) rows.Add(key[2..]);
            else if (key.StartsWith("R:", StringComparison.Ordinal) && key.Split(':') is [_, var row, var number] && short.TryParse(number, out var rack)) racks.Add(new(row, rack));
            else ModelState.AddModelError(nameof(Input.Targets), "Una asociación seleccionada no es válida.");
        }
        if (!ModelState.IsValid) { await Reload(token); return Page(); }
        var result = await processes.SaveProcessAsync(new(Input.OperationId, Input.Id, Input.Code, Input.Name, Input.IsActive, Input.ExpectedVersion, areas, rows, racks, Input.Reason, Input.Pin, Input.DefaultWipTargetKey, Input.InactivityAlertHours, Input.ReworkAlertHours), token);
        Input.Pin = "";
        if (result.Status == ProcessConfigurationStatus.Success) { TempData["Success"] = "Proceso guardado."; return RedirectToPage("Processes"); }
        ModelState.AddModelError(string.Empty, result.Status switch
        {
            ProcessConfigurationStatus.InvalidPin => "NIP ADMIN inválido.",
            ProcessConfigurationStatus.NotFound => "El proceso ya no existe. Regresa al catálogo y vuelve a seleccionarlo.",
            ProcessConfigurationStatus.ConcurrencyConflict => "La configuración cambió mientras editabas. Recarga y vuelve a revisar.",
            ProcessConfigurationStatus.IdempotencyConflict => "La operación ya se utilizó con datos distintos.",
            _ => result.Errors?.FirstOrDefault() ?? "No fue posible guardar el proceso."
        });
        await Reload(token); return Page();
    }
    private async Task Reload(CancellationToken token) { Process = await processes.GetAsync(Input.Id, token) ?? (await processes.GetAsync(Guid.Empty, token))!; }
    public sealed class InputModel
    {
        public Guid Id { get; set; }
        public Guid OperationId { get; set; } = Guid.NewGuid();
        [Required, StringLength(40)] public string Code { get; set; } = "";
        [Required, StringLength(120)] public string Name { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public uint ExpectedVersion { get; set; }
        public List<string> Targets { get; set; } = [];
        public string? DefaultWipTargetKey { get; set; }
        [Range(1, 8760)] public int? InactivityAlertHours { get; set; }
        [Range(1, 8760)] public int? ReworkAlertHours { get; set; }
        [StringLength(500)] public string? Reason { get; set; }
        [Required, RegularExpression("^[0-9]{4,8}$")] public string Pin { get; set; } = "";
    }

    public sealed record TargetSelection(string Key, string Label, string Type, string Description, bool IsAvailable);
}
