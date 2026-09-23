using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Web.Localization;
using WarehouseEPI.Web.Production;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionDailySetupTests
{
    [Fact]
    public void Missing_partial_and_inactive_configuration_cannot_capture()
    {
        var setup = Complete();
        Assert.True(setup.IsReady);
        Assert.False((setup with { Configuration = new(null, null, null, null, null, 0, false) }).IsReady);
        Assert.False((setup with { Configuration = setup.Configuration with { Shift2Id = null } }).IsReady);
        setup.Shifts[0].IsActive = false;
        Assert.False(setup.IsReady);
        Assert.Single(setup.CaptureShifts);
    }

    [Fact]
    public void Suggestions_require_one_active_match_and_never_replace_saved_associations()
    {
        var setup = Complete();
        Assert.Equal(setup.Stages[0].Id, setup.SuggestStage(null, "córte", "cut"));
        Assert.Equal(Guid.Empty, setup.SuggestStage(null, "missing"));
        Assert.Equal(setup.Shifts[1].Id, setup.SuggestShift(null, "t1", "shift 1"));
        Assert.Equal(setup.Shifts[0].Id, setup.SuggestShift(setup.Shifts[0].Id, "t1"));
        var ambiguous = setup with { Stages = [.. setup.Stages, new() { Code = "OTHER", Name = "Corte" }] };
        Assert.Equal(Guid.Empty, ambiguous.SuggestStage(null, "cut", "corte"));
        setup.Shifts[1].IsActive = false;
        Assert.Equal(Guid.Empty, setup.SuggestShift(setup.Shifts[1].Id, "t1"));
        Assert.Equal(Guid.Empty, setup.SuggestShift(null, "t1"));
    }

    [Fact]
    public void Capture_shifts_follow_T1_T2_not_catalog_name_order()
    {
        var setup = Complete();
        Assert.Equal(new[] { setup.Configuration.Shift1Id, setup.Configuration.Shift2Id }, setup.CaptureShifts.Select(x => (Guid?)x.Id));
    }

    [Theory]
    [InlineData(21, 21)]
    [InlineData(26, 21)]
    [InlineData(27, 21)]
    [InlineData(28, 28)]
    public void Week_start_uses_Monday_including_Sunday(int day, int monday) =>
        Assert.Equal(new DateOnly(2026, 9, monday), ProductionDailySetup.Monday(new(2026, 9, day)));

    [Fact]
    public void Default_week_prefers_current_then_latest_open_then_latest_available()
    {
        var current = Week(21, ProductionScheduleWeekStatus.Draft);
        var earlier = Week(7, ProductionScheduleWeekStatus.Open);
        var open = Week(14, ProductionScheduleWeekStatus.Open);
        var latest = Week(28, ProductionScheduleWeekStatus.Draft);
        var today = new DateOnly(2026, 9, 27);
        Assert.Equal(current.Id, ProductionDailySetup.DefaultWeek([latest, earlier, open, current], today));
        Assert.Equal(open.Id, ProductionDailySetup.DefaultWeek([latest, earlier, open], today));
        Assert.Equal(latest.Id, ProductionDailySetup.DefaultWeek([latest], today));
        Assert.Null(ProductionDailySetup.DefaultWeek([], today));
    }

    [Theory]
    [InlineData("Faltan 12.5 piezas programadas disponibles para repartir por FIFO.", "There are 12.5 scheduled pieces missing for FIFO allocation.")]
    [InlineData("OT-001: faltan 2.5 de SKU-Ñ surtido al proceso. Abre Surtimientos.", "OT-001: 2.5 of SKU-Ñ supplied to the process are missing. Open Production supplies.")]
    [InlineData("Línea 3 (SKU-Ñ): no tiene ruta activa.", "Line 3 (SKU-Ñ): does not have an active route.")]
    [InlineData("La cantidad no puede ser menor que lo ya procesado (1.5).", "The quantity cannot be less than the amount already processed (1.5).")]
    [InlineData("La semana debe estar en estado Open.", "The week must be in status Open.")]
    public void Message_arguments_are_preserved_and_not_used_as_resource_keys(string message, string expected)
    {
        using var services = new ServiceCollection().AddLogging().AddLocalization(options => options.ResourcesPath = "Resources").BuildServiceProvider();
        var texts = services.GetRequiredService<IStringLocalizer<ProductionTexts>>();
        var previous = CultureInfo.CurrentUICulture;
        var operational = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            Assert.Equal(expected, ProductionDailyText.Message(texts, message));
            Assert.Same(operational, CultureInfo.CurrentCulture);
            Assert.True(WarehouseEPI.Web.Pages.Operations.Production.ProductionQuantityBinder.TryParse("12.5", out var quantity));
            Assert.Equal(12.5m, quantity);
            Assert.False(WarehouseEPI.Web.Pages.Operations.Production.ProductionQuantityBinder.TryParse("12,5", out _));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private static ProductionScheduleWeekView Week(int day, ProductionScheduleWeekStatus status) =>
        new(Guid.NewGuid(), new(2026, 9, day), new DateOnly(2026, 9, day).AddDays(5), status, default, 0, []);

    private static ProductionDailySetup Complete()
    {
        ProductionStage[] stages = [new() { Code = "CUT", Name = "Corte" }, new() { Code = "SEW", Name = "Costura" }, new() { Code = "RTP", Name = "Ready to Pack" }];
        ProductionShift[] shifts = [new() { Code = "T2", Name = "A" }, new() { Code = "T1", Name = "Z" }];
        return new(new(stages[0].Id, stages[1].Id, stages[2].Id, shifts[1].Id, shifts[0].Id, 7, true), stages, shifts);
    }
}
