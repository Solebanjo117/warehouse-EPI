using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Locations;

public sealed class IndexModel(
    WarehouseDbContext dbContext,
    WarehouseMapService mapService,
    WipReportService wipReportService,
    HeatmapReportService heatmapReportService,
    ReportExportService reportExportService,
    WarehouseClock clock)
    : LocationIndexPageModel(dbContext, mapService, wipReportService, heatmapReportService, reportExportService, clock);
