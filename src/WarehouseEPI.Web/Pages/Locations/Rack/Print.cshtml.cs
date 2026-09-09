using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Locations.Rack;

public sealed class PrintModel(
    WarehouseDbContext dbContext,
    WarehouseClock warehouseClock,
    TimeProvider timeProvider)
    : RackPrintPageModel(dbContext, warehouseClock, timeProvider);

