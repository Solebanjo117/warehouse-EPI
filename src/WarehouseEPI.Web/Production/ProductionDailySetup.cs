using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Production;

public sealed record ProductionDailySetup(
    ProductionDailyConfigurationView Configuration,
    IReadOnlyList<ProductionStage> Stages,
    IReadOnlyList<ProductionShift> Shifts)
{
    public bool IsReady => Configuration.IsComplete &&
        new[] { Configuration.CuttingStageId, Configuration.SewingStageId, Configuration.ReadyToPackStageId }
            .Distinct().Count() == 3 &&
        new[] { Configuration.CuttingStageId, Configuration.SewingStageId, Configuration.ReadyToPackStageId }
            .All(id => Stages.Any(stage => stage.Id == id && stage.IsActive)) &&
        Configuration.Shift1Id != Configuration.Shift2Id &&
        new[] { Configuration.Shift1Id, Configuration.Shift2Id }.All(id => Shifts.Any(shift => shift.Id == id && shift.IsActive));

    public IReadOnlyList<ProductionShift> CaptureShifts =>
        new[] { Configuration.Shift1Id, Configuration.Shift2Id }
            .Select(id => Shifts.SingleOrDefault(shift => shift.Id == id && shift.IsActive)).OfType<ProductionShift>().DistinctBy(x => x.Id).ToArray();

    public static async Task<ProductionDailySetup> LoadAsync(ProductionDailyScheduleService service,
        WarehouseDbContext db, CancellationToken token) => new(await service.GetConfigurationAsync(token),
        await db.ProductionStages.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(token),
        await db.ProductionShifts.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(token));

    public Guid SuggestStage(Guid? saved, params string[] aliases) => Suggest(saved,
        Stages.Where(x => x.IsActive).Select(x => (x.Id, x.Code, x.Name)), aliases);
    public Guid SuggestShift(Guid? saved, params string[] aliases) => Suggest(saved,
        Shifts.Where(x => x.IsActive).Select(x => (x.Id, x.Code, x.Name)), aliases);

    private static Guid Suggest(Guid? saved, IEnumerable<(Guid Id, string Code, string Name)> choices, string[] aliases)
    {
        var rows = choices.ToArray();
        if (saved.HasValue) return rows.Any(x => x.Id == saved) ? saved.Value : Guid.Empty;
        var keys = aliases.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var matches = rows.Where(x => keys.Contains(Normalize(x.Code)) || keys.Contains(Normalize(x.Name)))
            .Select(x => x.Id).Distinct().Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : Guid.Empty;
    }

    private static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
        .Select(char.ToUpperInvariant).ToArray());

    public static DateOnly Monday(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
    public static Guid? DefaultWeek(IReadOnlyList<ProductionScheduleWeekView> weeks, DateOnly today) =>
        weeks.FirstOrDefault(x => x.WeekStart == Monday(today))?.Id ??
        weeks.Where(x => x.Status == ProductionScheduleWeekStatus.Open).OrderByDescending(x => x.WeekStart).FirstOrDefault()?.Id ??
        weeks.OrderByDescending(x => x.WeekStart).FirstOrDefault()?.Id;
}
