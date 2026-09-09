using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.Alerts;

[Authorize(Policy = "AdminOnly")]
public sealed class DetailsModel(
    OperationalExceptionService exceptions,
    WarehouseClock warehouseClock,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public OperationalExceptionDetailDto? Exception { get; private set; }
    public IReadOnlyList<OperationalExceptionAssigneeDto> Assignees { get; private set; } = [];
    public IReadOnlyList<LocalizedEvent> History { get; private set; } = [];
    public DateTimeOffset FirstDetectedAt { get; private set; }
    public DateTimeOffset LastDetectedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public string FirstDetectedRelative { get; private set; } = string.Empty;
    public string LastDetectedRelative { get; private set; } = string.Empty;
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        await LoadAsync(id, cancellationToken);
        return Exception is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) return Forbid();
        if (!ModelState.IsValid)
        {
            await LoadAsync(id, cancellationToken);
            return Exception is null ? NotFound() : Page();
        }
        var result = await exceptions.UpdateAsync(new(id, Input.OperationId, actor, Input.AssignedUserId, Input.Status, Input.Notes, Input.Version), cancellationToken);
        if (result.Status is OperationalExceptionUpdateStatus.Success or OperationalExceptionUpdateStatus.AlreadyApplied)
            return RedirectToPage(new { id });
        Error = result.Error ?? "No fue posible actualizar el caso.";
        await LoadAsync(id, cancellationToken);
        return Exception is null ? NotFound() : Page();
    }

    private async Task LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        Exception = await exceptions.GetDetailAsync(id, cancellationToken);
        Assignees = await exceptions.GetAssignableUsersAsync(cancellationToken);
        if (Exception is not null)
        {
            var now = timeProvider.GetUtcNow();
            FirstDetectedAt = await warehouseClock.ConvertAsync(Exception.Case.FirstDetectedAt, cancellationToken);
            LastDetectedAt = await warehouseClock.ConvertAsync(Exception.Case.LastDetectedAt, cancellationToken);
            ResolvedAt = Exception.Case.ResolvedAt is DateTimeOffset resolvedAt
                ? await warehouseClock.ConvertAsync(resolvedAt, cancellationToken)
                : null;
            FirstDetectedRelative = RelativeTime(Exception.Case.FirstDetectedAt, now);
            LastDetectedRelative = RelativeTime(Exception.Case.LastDetectedAt, now);

            var history = new List<LocalizedEvent>(Exception.Events.Count);
            foreach (var item in Exception.Events)
                history.Add(new(item, await warehouseClock.ConvertAsync(item.RecordedAt, cancellationToken)));
            History = history;
        }
        if (Exception is not null && Input.OperationId == Guid.Empty)
        {
            Input.OperationId = Guid.NewGuid();
            Input.AssignedUserId = Exception.Case.AssignedUserId;
            Input.Status = Exception.Case.Status;
            Input.Version = Exception.Case.Version;
        }
    }

    private static string RelativeTime(DateTimeOffset instant, DateTimeOffset now)
    {
        var elapsed = now - instant;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed < TimeSpan.FromMinutes(1)) return "hace menos de un minuto";
        if (elapsed < TimeSpan.FromHours(1)) return $"hace {(int)elapsed.TotalMinutes} min";
        if (elapsed < TimeSpan.FromDays(1)) return $"hace {(int)elapsed.TotalHours} h";
        var days = (int)elapsed.TotalDays;
        return days == 1 ? "hace 1 día" : $"hace {days} días";
    }

    public sealed record LocalizedEvent(OperationalExceptionEventDto Event, DateTimeOffset RecordedAt);

    public sealed class InputModel
    {
        public Guid OperationId { get; set; }
        public Guid? AssignedUserId { get; set; }
        public OperationalExceptionStatus Status { get; set; }
        [StringLength(500)] public string? Notes { get; set; }
        public uint Version { get; set; }
    }
}
