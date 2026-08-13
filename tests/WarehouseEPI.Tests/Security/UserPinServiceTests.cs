using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Security;

public sealed class UserPinServiceTests
{
    private const string LookupKey =
        "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task Authenticate_returns_active_user_only_for_correct_pin()
    {
        await using var dbContext = CreateDbContext();
        var service = new UserPinService(dbContext, new PinProtector(LookupKey));
        var user = CreateUser("Administrador", roleId: 1);

        Assert.Equal(PinAssignmentResult.Success, await service.AssignAsync(user, "0123"));
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var authenticated = await service.AuthenticateAsync("0123");

        Assert.NotNull(authenticated);
        Assert.Equal(user.Id, authenticated.Id);
        Assert.Equal("ADMIN", authenticated.Role.Code);
        Assert.Null(await service.AuthenticateAsync("0124"));

        user.IsActive = false;
        await dbContext.SaveChangesAsync();

        Assert.Null(await service.AuthenticateAsync("0123"));
    }

    [Fact]
    public async Task Assign_rejects_pin_used_by_another_user()
    {
        await using var dbContext = CreateDbContext();
        var service = new UserPinService(dbContext, new PinProtector(LookupKey));
        var first = CreateUser("Primer usuario", roleId: 1);
        var second = CreateUser("Segundo usuario", roleId: 2);

        Assert.Equal(PinAssignmentResult.Success, await service.AssignAsync(first, "4567"));
        dbContext.Users.Add(first);
        await dbContext.SaveChangesAsync();

        var result = await service.AssignAsync(second, "4567");

        Assert.Equal(PinAssignmentResult.Duplicate, result);
        Assert.Equal(string.Empty, second.PinLookup);
        Assert.Equal(string.Empty, second.PinHash);
    }

    [Fact]
    public async Task Assign_rejects_invalid_format()
    {
        await using var dbContext = CreateDbContext();
        var service = new UserPinService(dbContext, new PinProtector(LookupKey));
        var user = CreateUser("Usuario", roleId: 2);

        var result = await service.AssignAsync(user, "12A4");

        Assert.Equal(PinAssignmentResult.InvalidFormat, result);
    }

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var dbContext = new WarehouseDbContext(options);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static User CreateUser(string fullName, short roleId)
    {
        return new User
        {
            FullName = fullName,
            RoleId = roleId,
            PinLookup = string.Empty,
            PinHash = string.Empty
        };
    }
}
