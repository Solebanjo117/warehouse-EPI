using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Locations;

public sealed class DetailsModel(
    WarehouseDbContext dbContext,
    ProductLocationAssignmentService assignmentService)
    : LocationDetailsPageModel(dbContext, assignmentService);

