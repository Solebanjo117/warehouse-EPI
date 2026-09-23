using Microsoft.AspNetCore.Authorization;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Pages.Locations.Rack;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Locations.Rack;

[Authorize(Policy = "AdminOnly")]
public sealed class PrintModel(
    WarehouseDbContext dbContext,
    WarehouseClock warehouseClock,
    TimeProvider timeProvider)
    : RackPrintPageModel(dbContext, warehouseClock, timeProvider)
{
    public override bool IsAdministrativeView => true;

    internal PrintModel(WarehouseDbContext dbContext) : this(
        dbContext,
        new WarehouseClock(new WarehouseSettingsService(dbContext)),
        TimeProvider.System)
    { }
}
