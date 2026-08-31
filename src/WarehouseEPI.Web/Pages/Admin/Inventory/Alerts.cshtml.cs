using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Reporting;

namespace WarehouseEPI.Web.Pages.Admin.Inventory;

[Authorize(Policy = "AdminOnly")]
public sealed class AlertsModel(
    OperationalExceptionService exceptions,
    ILogger<AlertsModel> logger) : PageModel
{
    private const int PageSize = 25;
    private static readonly Action<ILogger, Exception?> ReconciliationFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(2102, "OperationalExceptionManualReconciliationFailed"),
        "No fue posible reconciliar las condiciones desde el centro de excepciones.");
    public OperationalExceptionStatus? Status { get; private set; }
    public OperationalExceptionCategory? Category { get; private set; }
    public OperationalExceptionSeverity? Severity { get; private set; }
    public Guid? AssignedUserId { get; private set; }
    public string? Search { get; private set; }
    public OperationalExceptionPageDto ExceptionPage { get; private set; } = new([], 0, 1, PageSize, 0, 0, 0, 0);
    public IReadOnlyList<OperationalExceptionAssigneeDto> Assignees { get; private set; } = [];
    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(ExceptionPage.TotalCount / (double)PageSize));

    public async Task OnGetAsync(string? view, string? attention, OperationalExceptionStatus? status,
        OperationalExceptionCategory? category, OperationalExceptionSeverity? severity, Guid? assignedUserId,
        string? search, int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        Status = status;
        Category = category ?? LegacyCategory(view, attention);
        Severity = severity;
        AssignedUserId = assignedUserId;
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        await LoadAsync(pageNumber, cancellationToken);
    }

    public async Task<IActionResult> OnPostRefreshAsync(string? view, string? attention, OperationalExceptionStatus? status,
        OperationalExceptionCategory? category, OperationalExceptionSeverity? severity, Guid? assignedUserId,
        string? search, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await exceptions.ReconcileAsync(cancellationToken);
            StatusMessage = $"Condiciones actualizadas: {result.Created} nueva(s), {result.Updated} actualizada(s), {result.Resolved} resuelta(s).";
            return RedirectToPage(new { view, attention, status, category = category ?? LegacyCategory(view, attention), severity, assignedUserId, search });
        }
        catch (Exception exception)
        {
            ReconciliationFailed(logger, exception);
            Status = status;
            Category = category ?? LegacyCategory(view, attention);
            Severity = severity;
            AssignedUserId = assignedUserId;
            Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
            ErrorMessage = "No fue posible actualizar las condiciones. El inventario no se modificó; revisa el registro del servidor.";
            await LoadAsync(1, cancellationToken);
            return Page();
        }
    }

    private async Task LoadAsync(int pageNumber, CancellationToken cancellationToken)
    {
        Assignees = await exceptions.GetAssignableUsersAsync(cancellationToken);
        ExceptionPage = await exceptions.GetPageAsync(new(Status, Category, Severity, AssignedUserId, Search, pageNumber, PageSize), cancellationToken);
    }

    private static OperationalExceptionCategory? LegacyCategory(string? view, string? attention) => view?.ToLowerInvariant() switch
    {
        "negative" => OperationalExceptionCategory.NegativeInventory,
        "minimum" => OperationalExceptionCategory.BelowMinimum,
        "unassigned" => OperationalExceptionCategory.UnassignedBalance,
        "restricted" => OperationalExceptionCategory.RestrictedInventory,
        "stagnant" => OperationalExceptionCategory.StagnantInventory,
        "cycle" when attention?.Equals("stale", StringComparison.OrdinalIgnoreCase) == true => OperationalExceptionCategory.CycleCountStale,
        "cycle" => OperationalExceptionCategory.CycleCountPending,
        "wip" => OperationalExceptionCategory.AgedWip,
        _ => null
    };
}
