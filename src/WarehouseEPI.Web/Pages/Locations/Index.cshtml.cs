using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Locations;

public sealed class IndexModel(
    WarehouseDbContext dbContext,
    WarehouseMapService mapService,
    WipReportService wipReportService)
    : LocationIndexPageModel(dbContext, mapService, wipReportService);

