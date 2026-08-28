using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Labels;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Operations.PalletLabels;

public sealed class IndexModel(LabelTemplateService templates, LabelDocumentService documents,
    PalletLicensePlateService plates, WarehouseClock clock) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public PalletLicensePlateEntry? Entry { get; private set; }
    public DateTimeOffset? EntryLocalTime { get; private set; }
    public LabelRenderDocument? Preview { get; private set; }
    public IReadOnlyList<string> PrintWarnings { get; private set; } = [];
    public string? SearchError { get; private set; }
    public IReadOnlyList<RecentEntry> Recent { get; private set; } = [];

    public sealed record RecentEntry(PalletLicensePlateCandidate Candidate, DateTimeOffset LocalTime);

    public async Task OnGetAsync(string? folio, Guid? id, CancellationToken token)
    {
        if (id is Guid movementId) await LoadEntryAsync(movementId, token);
        else if (!string.IsNullOrWhiteSpace(folio))
        {
            if (!PalletLicensePlateService.TryParseFolio(folio, out movementId)) SearchError = "Escribe un UUID de Entrada o un folio PLT válido.";
            else await LoadEntryAsync(movementId, token);
        }

        await LoadRecentAsync(token);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        if (Input.MovementId == Guid.Empty) ModelState.AddModelError("Input.MovementId", "Busca una Entrada confirmada.");
        if (Input.Weight?.Length > 40 || ContainsUnsafeText(Input.Weight)) ModelState.AddModelError("Input.Weight", "El peso debe tener hasta 40 caracteres imprimibles.");
        await LoadEntryAsync(Input.MovementId, token);
        await LoadRecentAsync(token);
        if (!ModelState.IsValid || Entry is null) return Page();

        var choice = await templates.GetPublishedByCodeAsync("PLT-LICENSE-PLATE", LabelTemplateKind.PalletLicensePlate, token);
        var version = choice is null ? null : await templates.GetPublishedEntityAsync(choice.VersionId, token);
        if (version is null) { ModelState.AddModelError(string.Empty, "La plantilla de placa no está publicada."); return Page(); }
        var localDate = (await clock.ConvertAsync(Entry.OccurredAt, token)).Date;
        var result = documents.Render(version, PalletLicensePlateService.Product(Entry),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["weight"] = Input.Weight?.Trim() ?? string.Empty },
            Input.Copies, PalletLicensePlateService.SystemValues(Entry, DateOnly.FromDateTime(localDate)));
        foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error);
        PrintWarnings = result.Warnings;
        Preview = result.Document;
        return Page();
    }

    private async Task LoadEntryAsync(Guid movementId, CancellationToken token)
    {
        if (movementId == Guid.Empty) return;
        var result = await plates.LoadAsync(movementId, token);
        if (result.Status == PalletLicensePlateStatus.Success)
        {
            Entry = result.Entry;
            Input.MovementId = movementId;
            EntryLocalTime = await clock.ConvertAsync(result.Entry!.OccurredAt, token);
        }
        else SearchError = result.Error;
    }

    private async Task LoadRecentAsync(CancellationToken token)
    {
        var candidates = await plates.RecentAsync(RecentCount, token);
        var rows = new List<RecentEntry>(candidates.Count);
        foreach (var candidate in candidates)
            rows.Add(new(candidate, await clock.ConvertAsync(candidate.OccurredAt, token)));
        Recent = rows;
    }

    private const int RecentCount = 8;

    private static bool ContainsUnsafeText(string? value) => value?.Any(character => char.IsControl(character) || character is '<' or '>') == true;

    public sealed class InputModel
    {
        public Guid MovementId { get; set; }
        [StringLength(40)] public string? Weight { get; set; }
        [Range(1, 100)] public int Copies { get; set; } = 1;
    }
}
