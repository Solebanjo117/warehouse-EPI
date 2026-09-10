using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Web;

public sealed class UnifiedMovementRouteTests
{
    [Fact]
    public void Canonical_movement_page_exposes_both_populations_shared_filters_and_detail_links()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Index.cshtml");
        var model = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Index.cshtml.cs");

        Assert.Contains("<h1 class=\"h2 mb-1\">Movimientos</h1>", page, StringComparison.Ordinal);
        Assert.Contains("RouteValues", page, StringComparison.Ordinal);
        Assert.Contains("asp-all-route-data", page, StringComparison.Ordinal);
        Assert.Contains("name=\"movementType\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"purpose\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"sku\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"locationCode\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"state\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-returnUrl=\"@returnUrl\"", page, StringComparison.Ordinal);
        Assert.Contains("MovementReportService", model, StringComparison.Ordinal);
        Assert.Contains("InventoryHistoryService", model, StringComparison.Ordinal);
        Assert.Contains("movementType ?? type", model, StringComparison.Ordinal);
        Assert.Contains("GetTraceExportAsync", model, StringComparison.Ordinal);
        Assert.Contains("period is \"today\" or \"yesterday\" or \"this-week\" or \"last-week\" or \"7\" or \"this-month\" or \"last-month\" or \"30\" or \"all\" or \"custom\"", model, StringComparison.Ordinal);
        Assert.Contains("Esta semana", page, StringComparison.Ordinal);
        Assert.Contains("Mes actual", page, StringComparison.Ordinal);
        Assert.Contains("Period == \"all\"", model, StringComparison.Ordinal);
        Assert.Contains("Period != \"custom\"", model, StringComparison.Ordinal);
        Assert.Contains("data-filter-chip", page, StringComparison.Ordinal);
        Assert.Contains("report-cards", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_has_one_movement_entry_and_legacy_route_redirects_with_export_compatibility()
    {
        var layout = Read("src", "WarehouseEPI.Web", "Pages", "Shared", "_Layout.cshtml");
        var legacy = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Reports", "Movements", "Index.cshtml.cs");

        Assert.Equal(1, Count(layout, "asp-page=\"/Admin/Inventory/Movements/Index\""));
        Assert.DoesNotContain("asp-page=\"/Admin/Reports/Movements/Index\"", layout, StringComparison.Ordinal);
        Assert.Contains("/Admin/Inventory/Movements", legacy, StringComparison.Ordinal);
        Assert.Contains("view=effective", legacy, StringComparison.Ordinal);
        Assert.Contains("OnGetExport() => OnGet()", legacy, StringComparison.Ordinal);
    }

    [Fact]
    public void Movement_detail_validates_local_return_targets_and_preserves_them_through_correction_links()
    {
        var detail = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Details.cshtml");
        var detailModel = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Details.cshtml.cs");
        var correct = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Correct.cshtml");
        var correctModel = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Correct.cshtml.cs");

        Assert.Contains("Url.IsLocalUrl(returnUrl)", detailModel, StringComparison.Ordinal);
        Assert.Contains("href=\"@Model.ReturnUrl\"", detail, StringComparison.Ordinal);
        Assert.Contains("asp-route-returnUrl=\"@Model.ReturnUrl\"", detail, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"ReturnUrl\" type=\"hidden\"", correct, StringComparison.Ordinal);
        Assert.Contains("Url.IsLocalUrl(returnUrl)", correctModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Movement_detail_is_the_professional_printable_traceability_record()
    {
        var detail = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Details.cshtml");
        var detailModel = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Details.cshtml.cs");
        var styles = Read("src", "WarehouseEPI.Web", "wwwroot", "css", "movement-details.css");
        var program = Read("src", "WarehouseEPI.Web", "Program.cs");

        Assert.Contains("MovementTraceabilityService", detailModel, StringComparison.Ordinal);
        Assert.Contains("WarehouseSettingsService", detailModel, StringComparison.Ordinal);
        Assert.Contains("[Authorize(Policy = \"AdminOnly\")]", detailModel, StringComparison.Ordinal);
        Assert.Contains("data-movement-traceability", detail, StringComparison.Ordinal);
        Assert.Contains("data-print-page", detail, StringComparison.Ordinal);
        Assert.Contains("Eventos relacionados", detail, StringComparison.Ordinal);
        Assert.Contains("No existen eventos adicionales relacionados", detail, StringComparison.Ordinal);
        Assert.Contains("d-print-none", detail, StringComparison.Ordinal);
        Assert.Contains("movement-details.css", detail, StringComparison.Ordinal);
        Assert.Contains("@page { size: Letter portrait;", styles, StringComparison.Ordinal);
        Assert.Contains(".movement-detail-header { display: flex !important;", styles, StringComparison.Ordinal);
        Assert.Contains(".movement-line-header { display: flex !important;", styles, StringComparison.Ordinal);
        Assert.Contains("AddScoped<MovementTraceabilityService>()", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Current_entry_movements_expose_a_pallet_plate_action_without_adding_it_to_audit_rows()
    {
        var index = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Index.cshtml");
        var indexModel = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Index.cshtml.cs");
        var detail = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Details.cshtml");
        var detailModel = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Details.cshtml.cs");

        Assert.Contains("CanGeneratePalletPlate(item)", index, StringComparison.Ordinal);
        Assert.Equal(1, Count(index, "asp-page=\"/Operations/PalletLabels/Index\""));
        Assert.Contains("PalletLicensePlateService.IsEligible", indexModel, StringComparison.Ordinal);
        Assert.Contains("CanGeneratePalletPlate", detail, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Operations/PalletLabels/Index\"", detail, StringComparison.Ordinal);
        Assert.Contains("PalletLicensePlateService.IsEligible", detailModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Movements_quick_periods_filter_real_data_across_calendar_boundaries_and_export()
    {
        await using var db = CreateDbContext();
        var settingsService = new WarehouseSettingsService(db);
        var settings = await settingsService.GetTrackedAsync();
        settings.TimeZoneId = "America/Matamoros";
        await db.SaveChangesAsync();

        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Matamoros");

        var user = new User { FullName = "Admin", PinLookup = "lk", PinHash = "ph", RoleId = 1 };
        var product = new Product { Sku = "SKU-CAL", BaseUnitId = 1, IsActive = true };
        var loc = new Location { Code = "CAL-01", Kind = LocationKind.Rack, IsActive = true };
        db.AddRange(user, product, loc);

        void AddMovement(DateTime localDateTime, string refCode)
        {
            var utcInstant = TimeZoneInfo.ConvertTimeToUtc(localDateTime, tz);
            var mov = new InventoryMovement
            {
                Type = InventoryMovementType.Entry,
                Purpose = InventoryMovementPurpose.Standard,
                ResponsibleUser = user,
                OccurredAt = utcInstant,
                Reference = refCode,
                RequestFingerprint = Guid.NewGuid().ToString()
            };
            mov.Lines.Add(new InventoryMovementLine
            {
                Product = product,
                UnitId = 1,
                DestinationLocation = loc,
                Quantity = 1m,
                LineNumber = 1
            });
            db.InventoryMovements.Add(mov);
        }

        // ==========================================
        // 1. Escenario Semana y Lunes
        // Referencia: Lunes 24 de agosto de 2026 a las 10:00 local
        // ==========================================
        var mondayLocal = new DateTime(2026, 8, 24, 10, 0, 0);
        var mondayUtc = TimeZoneInfo.ConvertTimeToUtc(mondayLocal, tz);
        var timeProvider = new MutableTimeProvider(mondayUtc);

        AddMovement(new DateTime(2026, 8, 24, 9, 30, 0), "MOV-MON-TODAY");
        AddMovement(new DateTime(2026, 8, 23, 16, 0, 0), "MOV-SUN-YESTERDAY"); // Domingo previo (semana pasada)
        AddMovement(new DateTime(2026, 8, 19, 14, 0, 0), "MOV-LAST-WED");     // Miércoles semana pasada
        AddMovement(new DateTime(2026, 8, 17, 8, 0, 0), "MOV-LAST-MON");      // Lunes semana pasada (inicio)
        AddMovement(new DateTime(2026, 8, 16, 20, 0, 0), "MOV-TWO-WEEKS-AGO"); // Domingo hace 2 semanas
        await db.SaveChangesAsync();

        var model = CreateMovementsModel(db, timeProvider);

        // a) today: sólo MOV-MON-TODAY
        await model.OnGetAsync("effective", null, null, null, null, null, period: "today");
        Assert.Single(model.EffectiveResults.Items);
        Assert.Equal("MOV-MON-TODAY", model.EffectiveResults.Items[0].Reference);

        // b) yesterday: sólo MOV-SUN-YESTERDAY
        await model.OnGetAsync("effective", null, null, null, null, null, period: "yesterday");
        Assert.Single(model.EffectiveResults.Items);
        Assert.Equal("MOV-SUN-YESTERDAY", model.EffectiveResults.Items[0].Reference);

        // c) this-week en día lunes: únicamente hoy (lunes)
        await model.OnGetAsync("effective", null, null, null, null, null, period: "this-week");
        Assert.Single(model.EffectiveResults.Items);
        Assert.Equal("MOV-MON-TODAY", model.EffectiveResults.Items[0].Reference);

        // d) last-week en día lunes: lunes 17 a domingo 23 (3 movimientos)
        await model.OnGetAsync("effective", null, null, null, null, null, period: "last-week");
        Assert.Equal(3, model.EffectiveResults.Items.Count);
        var refsLastWeek = model.EffectiveResults.Items.Select(x => x.Reference).ToHashSet();
        Assert.Contains("MOV-SUN-YESTERDAY", refsLastWeek);
        Assert.Contains("MOV-LAST-WED", refsLastWeek);
        Assert.Contains("MOV-LAST-MON", refsLastWeek);
        Assert.DoesNotContain("MOV-MON-TODAY", refsLastWeek);
        Assert.DoesNotContain("MOV-TWO-WEEKS-AGO", refsLastWeek);

        // e) exportación CSV de last-week
        var exportResult = Assert.IsType<FileContentResult>(
            await model.OnGetExportAsync("csv", "effective", null, null, null, null, null, period: "last-week"));
        var csv = System.Text.Encoding.UTF8.GetString(exportResult.FileContents[3..]);
        Assert.Contains("MOV-SUN-YESTERDAY", csv);
        Assert.Contains("MOV-LAST-WED", csv);
        Assert.Contains("MOV-LAST-MON", csv);
        Assert.DoesNotContain("MOV-MON-TODAY", csv);
        Assert.DoesNotContain("MOV-TWO-WEEKS-AGO", csv);

        // ==========================================
        // 2. Escenario Cambio de Mes y de Año
        // Referencia: 5 de enero de 2026 (mes actual = enero 2026, mes anterior = diciembre 2025)
        // ==========================================
        var janLocal = new DateTime(2026, 1, 5, 11, 0, 0);
        timeProvider.Set(TimeZoneInfo.ConvertTimeToUtc(janLocal, tz));

        AddMovement(new DateTime(2026, 1, 5, 10, 0, 0), "MOV-JAN-05");
        AddMovement(new DateTime(2026, 1, 1, 8, 30, 0), "MOV-JAN-01");
        AddMovement(new DateTime(2025, 12, 31, 22, 0, 0), "MOV-DEC-31"); // Fin de año previo
        AddMovement(new DateTime(2025, 12, 1, 9, 0, 0), "MOV-DEC-01");   // Inicio de mes previo
        AddMovement(new DateTime(2025, 11, 30, 18, 0, 0), "MOV-NOV-30"); // Noviembre
        await db.SaveChangesAsync();

        // this-month (1 al 5 de enero de 2026)
        await model.OnGetAsync("effective", null, null, null, null, null, period: "this-month");
        Assert.Equal(2, model.EffectiveResults.Items.Count);
        var refsThisMonth = model.EffectiveResults.Items.Select(x => x.Reference).ToHashSet();
        Assert.Contains("MOV-JAN-05", refsThisMonth);
        Assert.Contains("MOV-JAN-01", refsThisMonth);
        Assert.DoesNotContain("MOV-DEC-31", refsThisMonth);

        // last-month (1 al 31 de diciembre de 2025 del año previo)
        await model.OnGetAsync("effective", null, null, null, null, null, period: "last-month");
        Assert.Equal(2, model.EffectiveResults.Items.Count);
        var refsLastMonth = model.EffectiveResults.Items.Select(x => x.Reference).ToHashSet();
        Assert.Contains("MOV-DEC-31", refsLastMonth);
        Assert.Contains("MOV-DEC-01", refsLastMonth);
        Assert.DoesNotContain("MOV-JAN-01", refsLastMonth);
        Assert.DoesNotContain("MOV-NOV-30", refsLastMonth);

        // ==========================================
        // 3. Escenario Febrero Bisiesto vs No Bisiesto
        // Referencia A: 1 de marzo de 2028 (2028 es bisiesto, febrero tiene 29 días)
        // ==========================================
        var mar2028Local = new DateTime(2028, 3, 1, 10, 0, 0);
        timeProvider.Set(TimeZoneInfo.ConvertTimeToUtc(mar2028Local, tz));

        AddMovement(new DateTime(2028, 2, 29, 15, 0, 0), "MOV-LEAP-FEB-29"); // Día bisiesto
        AddMovement(new DateTime(2028, 2, 1, 8, 0, 0), "MOV-LEAP-FEB-01");
        AddMovement(new DateTime(2028, 1, 31, 23, 0, 0), "MOV-LEAP-JAN-31");
        AddMovement(new DateTime(2028, 3, 1, 9, 0, 0), "MOV-LEAP-MAR-01");
        await db.SaveChangesAsync();

        await model.OnGetAsync("effective", null, null, null, null, null, period: "last-month");
        Assert.Equal(2, model.EffectiveResults.Items.Count);
        var refsLeap = model.EffectiveResults.Items.Select(x => x.Reference).ToHashSet();
        Assert.Contains("MOV-LEAP-FEB-29", refsLeap);
        Assert.Contains("MOV-LEAP-FEB-01", refsLeap);
        Assert.DoesNotContain("MOV-LEAP-JAN-31", refsLeap);
        Assert.DoesNotContain("MOV-LEAP-MAR-01", refsLeap);

        // Referencia B: 1 de marzo de 2026 (2026 NO es bisiesto, febrero termina en 28)
        var mar2026Local = new DateTime(2026, 3, 1, 10, 0, 0);
        timeProvider.Set(TimeZoneInfo.ConvertTimeToUtc(mar2026Local, tz));

        AddMovement(new DateTime(2026, 2, 28, 20, 0, 0), "MOV-NONLEAP-FEB-28");
        AddMovement(new DateTime(2026, 3, 1, 8, 0, 0), "MOV-NONLEAP-MAR-01");
        await db.SaveChangesAsync();

        await model.OnGetAsync("effective", null, null, null, null, null, period: "last-month");
        var refsNonLeap = model.EffectiveResults.Items.Select(x => x.Reference).ToHashSet();
        Assert.Contains("MOV-NONLEAP-FEB-28", refsNonLeap);
        Assert.DoesNotContain("MOV-NONLEAP-MAR-01", refsNonLeap);
    }

    private static WarehouseEPI.Web.Pages.Admin.Inventory.Movements.IndexModel CreateMovementsModel(
        WarehouseDbContext db,
        TimeProvider timeProvider)
    {
        var settings = new WarehouseSettingsService(db);
        var clock = new WarehouseClock(settings);
        return new(
            new InventoryHistoryService(db),
            new MovementReportService(db),
            new ReportExportService(settings),
            clock,
            db,
            settings,
            timeProvider)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"UnifiedMovementRouteTests-{Guid.NewGuid():N}")
            .Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Set(DateTimeOffset next) => current = next;
    }

    private static int Count(string value, string search)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0; index += search.Length)
            count++;
        return count;
    }

    private static string Read(params string[] parts) => File.ReadAllText(RepositoryPath(parts));

    private static string RepositoryPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
        return Path.Combine([directory.FullName, .. parts]);
    }
}
