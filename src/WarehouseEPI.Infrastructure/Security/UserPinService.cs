using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Security;

public sealed class UserPinService(
    WarehouseDbContext dbContext,
    PinProtector pinProtector)
{
    private readonly string dummyHash = pinProtector.Hash("00000000");

    public async Task<User?> AuthenticateAsync(
        string pin,
        CancellationToken cancellationToken = default)
    {
        string lookup;

        try
        {
            lookup = pinProtector.CreateLookup(pin);
        }
        catch (PinFormatException)
        {
            return null;
        }

        var user = await dbContext.Users
            .AsNoTracking()
            .Include(candidate => candidate.Role)
            .SingleOrDefaultAsync(
                candidate => candidate.PinLookup == lookup,
                cancellationToken);

        var hash = user?.PinHash ?? dummyHash;
        var isValid = pinProtector.Verify(pin, hash);

        return isValid && user is { IsActive: true } ? user : null;
    }

    public async Task<PinAssignmentResult> AssignAsync(
        User user,
        string pin,
        CancellationToken cancellationToken = default)
    {
        string lookup;

        try
        {
            lookup = pinProtector.CreateLookup(pin);
        }
        catch (PinFormatException)
        {
            return PinAssignmentResult.InvalidFormat;
        }

        var duplicate = await dbContext.Users.AnyAsync(
            candidate => candidate.PinLookup == lookup && candidate.Id != user.Id,
            cancellationToken);

        if (duplicate)
        {
            return PinAssignmentResult.Duplicate;
        }

        user.PinLookup = lookup;
        user.PinHash = pinProtector.Hash(pin);
        return PinAssignmentResult.Success;
    }
}
