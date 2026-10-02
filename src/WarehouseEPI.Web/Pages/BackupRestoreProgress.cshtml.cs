using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Web.Backups;

namespace WarehouseEPI.Web.Pages;

[AllowAnonymous]
public sealed class BackupRestoreProgressModel(RestoreJobStore store) : PageModel
{
    public Guid? Id { get; private set; }
    public RestoreJobState? State { get; private set; }
    public bool Running => State != RestoreJobState.RecoveryRequired && (store.IsMaintenance || State is RestoreJobState.Requested or RestoreJobState.Running);
    public IActionResult OnGet(Guid? id)
    {
        Response.Headers.CacheControl = "no-store";
        Id = id ?? store.ActiveId;
        if (Id is { } value)
        {
            var job = store.Get(value);
            if (job is null) return NotFound();
            State = job.State;
        }
        return Page();
    }
    public IActionResult OnGetStatus(Guid? id)
    {
        Response.Headers.CacheControl = "no-store";
        var job = id is { } value ? store.Get(value) : null;
        return id is not null && job is null ? NotFound() : new JsonResult(new
        { running = job?.State != RestoreJobState.RecoveryRequired && (store.IsMaintenance || job?.State is RestoreJobState.Requested or RestoreJobState.Running) });
    }
}
