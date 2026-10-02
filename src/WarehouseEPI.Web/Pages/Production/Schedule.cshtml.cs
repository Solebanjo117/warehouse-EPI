using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Production;

public sealed class ScheduleModel(ProductionDailyScheduleService schedules) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid? WeekId { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public IReadOnlyList<ProductionScheduleWeekView> Weeks { get; private set; } = [];
    public ProductionScheduleWeekView? Week { get; private set; }
    public IReadOnlyList<ProductionScheduleLineView> Lines { get; private set; } = [];
    public IReadOnlyList<ProductionSchedulePlanSummaryRow> Summary { get; private set; } = [];
    public int PageCount { get; private set; } = 1;

    public async Task<IActionResult> OnGetAsync(CancellationToken token)
    {
        Weeks = await schedules.ListWeeksAsync(token);
        WeekId ??= Weeks.FirstOrDefault()?.Id;
        if (WeekId is not Guid id) return Page();
        Week = await schedules.GetWeekAsync(id, token);
        if (Week is null) return NotFound();
        PageCount = Math.Max(1, (int)Math.Ceiling(Week.Lines.Count / 25d));
        PageNumber = Math.Clamp(PageNumber, 1, PageCount);
        Lines = Week.Lines.OrderBy(x => x.PlannedDate).ThenBy(x => x.Sequence).ThenBy(x => x.Id)
            .Skip((PageNumber - 1) * 25).Take(25).ToArray();
        Summary = await schedules.GetPlanSummaryAsync(id, token);
        return Page();
    }
}
