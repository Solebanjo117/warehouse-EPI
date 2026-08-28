using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Locations;

public enum ProductLocationAssignmentResult
{
    Success,
    AlreadyActive,
    ProductNotFound,
    ProductInactive,
    LocationNotFound,
    LocationInactive,
    LocationBlocked,
    LocationDoesNotTrackInventory,
    AssignmentNotFound
}

public sealed class ProductLocationAssignmentService(WarehouseDbContext dbContext)
{
    public async Task<ProductLocationAssignmentResult> AssignAsync(
        Guid productId,
        Guid locationId,
        CancellationToken cancellationToken = default)
    {
        var product = await dbContext.Products.SingleOrDefaultAsync(
            candidate => candidate.Id == productId, cancellationToken);
        if (product is null) return ProductLocationAssignmentResult.ProductNotFound;
        if (!product.IsActive) return ProductLocationAssignmentResult.ProductInactive;

        var location = await dbContext.Locations.SingleOrDefaultAsync(
            candidate => candidate.Id == locationId, cancellationToken);
        if (location is null) return ProductLocationAssignmentResult.LocationNotFound;
        if (!location.IsPhysicallyPresent || !location.IsActive) return ProductLocationAssignmentResult.LocationInactive;
        if (location.IsBlocked) return ProductLocationAssignmentResult.LocationBlocked;
        var assignment = await dbContext.ProductLocationAssignments.SingleOrDefaultAsync(
            candidate => candidate.ProductId == productId && candidate.LocationId == locationId,
            cancellationToken);
        if (assignment?.IsActive == true) return ProductLocationAssignmentResult.AlreadyActive;

        if (assignment is null)
        {
            dbContext.ProductLocationAssignments.Add(new ProductLocationAssignment
            {
                ProductId = productId,
                LocationId = locationId
            });
        }
        else
        {
            assignment.IsActive = true;
            assignment.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ProductLocationAssignmentResult.Success;
    }

    public async Task<ProductLocationAssignmentResult> DeactivateAsync(
        Guid productId,
        Guid locationId,
        CancellationToken cancellationToken = default)
    {
        var assignment = await dbContext.ProductLocationAssignments.SingleOrDefaultAsync(
            candidate => candidate.ProductId == productId && candidate.LocationId == locationId,
            cancellationToken);
        if (assignment is null) return ProductLocationAssignmentResult.AssignmentNotFound;
        if (!assignment.IsActive) return ProductLocationAssignmentResult.AssignmentNotFound;

        assignment.IsActive = false;
        assignment.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ProductLocationAssignmentResult.Success;
    }
}
