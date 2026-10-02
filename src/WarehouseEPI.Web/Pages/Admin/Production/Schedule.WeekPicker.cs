using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Web.Production;

namespace WarehouseEPI.Web.Pages.Admin.Production;

public sealed partial class ScheduleModel
{
    public sealed record WeekPickerOption(string WeekStart, string WeekEnd, string Label, string? ViewUrl);
    public sealed record WeekPickerMonth(string Month, string Label, string? PreviousMonth,
        string? NextMonth, IReadOnlyList<WeekPickerOption> Options);

    public WeekPickerMonth? NewWeekMonth { get; private set; }
    public string CurrentWeekStart { get; private set; } = "";
    public string NextWeekStart { get; private set; } = "";
    public WeekPickerOption? NewWeekSelection => NewWeekMonth?.Options
        .FirstOrDefault(option => option.WeekStart == IsoDay(NewWeek.WeekStart));
    public bool CanCreateSelectedWeek => ConfigurationReady && NewWeekSelection is { ViewUrl: null };

    public async Task<IActionResult> OnGetWeekOptionsAsync(string? month, CancellationToken token)
    {
        if (month?.Length != 7 || !DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(await WeekOptionsAsync(first, token));
    }

    private async Task LoadWeekPickerAsync(DateOnly today, bool initialize, CancellationToken token)
    {
        if (ActionPanel != "new" && Weeks.Count != 0) return;
        var monday = ProductionDailySetup.Monday(today);
        CurrentWeekStart = IsoDay(monday)!;
        NextWeekStart = monday <= DateOnly.MaxValue.AddDays(-13) ? IsoDay(monday.AddDays(7))! : "";
        if (initialize)
        {
            // ListWeeksAsync includes lines and stops at 60; only dates are needed here.
            var starts = await db.ProductionScheduleWeeks.AsNoTracking()
                .Where(week => week.WeekStart >= monday).OrderBy(week => week.WeekStart)
                .Select(week => week.WeekStart).ToListAsync(token);
            var candidate = monday;
            foreach (var start in starts)
            {
                if (start > candidate) break;
                if (start != candidate) continue;
                if (candidate > DateOnly.MaxValue.AddDays(-13))
                {
                    candidate = DateOnly.MinValue;
                    break;
                }
                candidate = candidate.AddDays(7);
            }
            NewWeek.WeekStart = candidate;
        }
        var selected = NewWeek.WeekStart == DateOnly.MinValue ? monday : NewWeek.WeekStart;
        NewWeekMonth = await WeekOptionsAsync(new DateOnly(selected.Year, selected.Month, 1), token);
    }

    private async Task<WeekPickerMonth> WeekOptionsAsync(DateOnly first, CancellationToken token)
    {
        var last = new DateOnly(first.Year, first.Month, DateTime.DaysInMonth(first.Year, first.Month));
        var existing = await db.ProductionScheduleWeeks.AsNoTracking()
            .Where(week => week.WeekStart >= first && week.WeekStart <= last)
            .Select(week => new { week.WeekStart, week.Id }).ToDictionaryAsync(week => week.WeekStart, token);
        var options = new List<WeekPickerOption>();
        var start = first.AddDays(((int)DayOfWeek.Monday - (int)first.DayOfWeek + 7) % 7);
        while (start <= last && start <= DateOnly.MaxValue.AddDays(-ProductionWeekCalendar.LastDayOffset))
        {
            var end = ProductionWeekCalendar.End(start);
            var culture = CultureInfo.CurrentUICulture;
            var format = culture.TwoLetterISOLanguageName == "en" ? "MMMM d, yyyy" : "d 'de' MMMM 'de' yyyy";
            var startText = start.Month == end.Month && start.Year == end.Year
                ? start.Day.ToString(culture) : start.ToString(format, culture);
            options.Add(new(IsoDay(start)!, IsoDay(end)!,
                texts["Lunes {0} – domingo {1}", startText, end.ToString(format, culture)].Value,
                existing.TryGetValue(start, out var week)
                    ? Url.Page("Schedule", new { WeekId = week.Id, SelectedDay = IsoDay(start), View = "program" }) : null));
            if (start > DateOnly.MaxValue.AddDays(-7)) break;
            start = start.AddDays(7);
        }
        return new(first.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            first.ToString("MMMM yyyy", CultureInfo.CurrentUICulture),
            first > DateOnly.MinValue ? first.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture) : null,
            first.Year < 9999 || first.Month < 12 ? first.AddMonths(1).ToString("yyyy-MM", CultureInfo.InvariantCulture) : null,
            options);
    }
}
