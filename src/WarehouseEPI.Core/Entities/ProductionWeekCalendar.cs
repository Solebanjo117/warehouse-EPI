namespace WarehouseEPI.Core.Entities;

public static class ProductionWeekCalendar
{
    public const int DayCount = 7;
    public const int LastDayOffset = DayCount - 1;

    public static DateOnly End(DateOnly monday) => monday.AddDays(LastDayOffset);
    public static IEnumerable<DateOnly> Days(DateOnly monday) =>
        Enumerable.Range(0, DayCount).Select(monday.AddDays);
}
