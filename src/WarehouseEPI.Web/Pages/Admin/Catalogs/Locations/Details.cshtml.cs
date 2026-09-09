using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Pages.Locations;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Locations;

[Authorize(Policy = "AdminOnly")]
public sealed class DetailsModel(
    WarehouseDbContext dbContext,
    ProductLocationAssignmentService assignmentService)
    : LocationDetailsPageModel(dbContext, assignmentService)
{
    public override bool IsAdministrativeView => true;

    public async Task<IActionResult> OnPostAssignAsync(Guid id, Guid productId, CancellationToken cancellationToken)
    {
        var result = await AssignmentService.AssignAsync(productId, id, cancellationToken);
        SetResultMessage(result);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeactivateAsync(Guid id, Guid productId, CancellationToken cancellationToken)
    {
        var result = await AssignmentService.DeactivateAsync(productId, id, cancellationToken);
        if (result == ProductLocationAssignmentResult.Success) Message = "La asignación fue desactivada.";
        else Error = "La asignación activa ya no existe.";
        return RedirectToPage(new { id });
    }
}
