using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Web.Backups;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Admin.System;

[RequestSizeLimit(RestorePackageInspector.MaxUploadBytes + 1048576)]
[RequestFormLimits(MultipartBodyLengthLimit = RestorePackageInspector.MaxUploadBytes, ValueLengthLimit = 4096)]
public sealed class RestoreModel(RestoreUploadService uploads, RestoreJobStore store,
    TimeProvider time, IStringLocalizer<CatalogTexts> texts) : PageModel
{
    public RestoreJob? Job { get; private set; }
    public string? Error { get; private set; }
    public string? UnavailableReason => store.AgentUnavailableReason(time);
    public bool Validating => Job?.State == RestoreJobState.Validating;
    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public IActionResult OnGet(Guid? id)
    {
        if (id is { } value && (Job = store.Get(value, ActorId)) is null) return NotFound();
        return Page();
    }
    public async Task<IActionResult> OnPostUploadAsync(IFormFile? backup, string? password, CancellationToken token)
    {
        if (backup is null || password is null) Error = texts["Selecciona el respaldo e introduce su contraseña."];
        else
        {
            var result = await uploads.UploadAsync(ActorId, backup, password, token);
            if (result.Id is { } id) return RedirectToPage(new { id });
            Error = texts[result.Error!];
        }
        ModelState.Clear();
        return Page();
    }
    public IActionResult OnPostConfirm(Guid id, string? adminPin, bool replaceAccepted, string? confirmation)
    {
        if ((Job = store.Get(id, ActorId)) is null) return NotFound();
        var error = uploads.Confirm(id, ActorId, adminPin, replaceAccepted, confirmation);
        if (error is null) return RedirectToPage("/BackupRestoreProgress", new { id });
        Error = texts[error];
        ModelState.Clear();
        return Page();
    }
    public IActionResult OnGetStatus(Guid id)
    {
        Response.Headers.CacheControl = "no-store";
        var job = store.Get(id, ActorId);
        return job is null ? NotFound() : new JsonResult(new { running = job.State == RestoreJobState.Validating });
    }
}
