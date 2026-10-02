using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Backups;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Admin.System;

public sealed class BackupsModel(ManualBackupService backups, WarehouseClock clock, IStringLocalizer<CatalogTexts> texts) : PageModel
{
    public string? UnavailableReason { get; private set; }
    public string? Error { get; private set; }
    public ManualBackupRecord? Current { get; private set; }
    public IReadOnlyList<ManualBackupRecord> History { get; private set; } = [];
    public Dictionary<Guid, DateTimeOffset> LocalDates { get; } = [];
    public bool Running => Current?.State == ManualBackupState.Running;

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(string? password, string? confirmation, CancellationToken cancellationToken)
    {
        if (password is null || password.Length is < 12 or > 128)
            Error = texts["Usa una contraseña de entre 12 y 128 caracteres."];
        else if (!string.Equals(password, confirmation, StringComparison.Ordinal))
            Error = texts["Las contraseñas no coinciden."];
        else
        {
            var start = backups.Start(password);
            if (start.Id is not null) return RedirectToPage();
            Error = texts[start.Error!];
        }
        // Never echo a submitted backup password, including model-binding errors.
        ModelState.Clear();
        await LoadAsync(cancellationToken);
        return Page();
    }

    public JsonResult OnGetStatus() => new(new { running = backups.Current?.State == ManualBackupState.Running });

    public IActionResult OnGetDownload(Guid id)
    {
        var record = backups.History().FirstOrDefault(item => item.Id == id);
        if (record is null) return NotFound();
        var stream = backups.OpenDownload(id);
        return stream is null ? NotFound() : File(stream, "application/octet-stream", ManualBackupService.DownloadName(record));
    }

    private async Task LoadAsync(CancellationToken token)
    {
        UnavailableReason = backups.UnavailableReason;
        Current = backups.Current;
        History = backups.History();
        foreach (var record in History.Concat(Current is null ? [] : new[] { Current }).DistinctBy(item => item.Id))
            LocalDates[record.Id] = await clock.ConvertAsync(record.StartedAt, token);
    }
}
