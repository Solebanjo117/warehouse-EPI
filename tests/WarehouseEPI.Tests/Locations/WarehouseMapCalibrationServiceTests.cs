using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Locations;

public sealed class WarehouseMapCalibrationServiceTests
{
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const double OriginLatitude = 25.85;
    private const double OriginLongitude = -97.5;
    private const double EarthRadius = 6378137d;

    [Fact]
    public void Calculate_recovers_affine_transform_and_excludes_checks_from_fit()
    {
        var points = new[]
        {
            Point("R1", "REFERENCE", 0, 0), Point("R2", "REFERENCE", 20, 0),
            Point("R3", "REFERENCE", 0, 20), Point("R4", "REFERENCE", 20, 20),
            Point("C1", "CHECK", 8, 11, mapOffsetX: 6)
        };

        var result = WarehouseMapCalibrationService.Calculate(points, 1600, 900);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Transform);
        Assert.Equal(4, result.ReferenceCount);
        Assert.Equal(1, result.CheckCount);
        Assert.InRange(result.Transform!.A11, 1.999, 2.001);
        Assert.InRange(result.Transform.A12, 2.999, 3.001);
        Assert.InRange(result.Transform.A21, -1.001, -.999);
        Assert.InRange(result.Transform.A22, 3.999, 4.001);
        Assert.InRange(result.Transform.FitErrorMeters, 0, .001);
        Assert.True(result.Transform.CheckErrorMeters > 0);
    }

    [Fact]
    public void Calculate_rejects_collinear_references()
    {
        var points = new[]
        {
            Point("R1", "REFERENCE", 0, 0), Point("R2", "REFERENCE", 10, 0),
            Point("R3", "REFERENCE", 20, 0), Point("R4", "REFERENCE", 30, 0),
            Point("C1", "CHECK", 5, 4)
        };

        var result = WarehouseMapCalibrationService.Calculate(points, 1600, 900);

        Assert.Contains(result.Errors, error => error.Contains("una sola línea", StringComparison.Ordinal));
        Assert.Null(result.Transform);
    }

    [Fact]
    public async Task Publish_is_idempotent_and_layout_changes_make_calibration_pending()
    {
        await using var db = CreateDb();
        var role = new Role { Id = 1, Code = "ADMIN", Name = "Administrador" };
        var admin = new User { FullName = "Admin mapa", Role = role, RoleId = role.Id, PinLookup = "", PinHash = "" };
        var pins = new UserPinService(db, new PinProtector(Key));
        Assert.Equal(PinAssignmentResult.Success, await pins.AssignAsync(admin, "4826"));
        db.AddRange(role, admin, new WarehouseMapLayout { Id = 1, Version = 7, UpdatedByUserId = admin.Id });
        await db.SaveChangesAsync();
        var service = new WarehouseMapCalibrationService(db, pins);
        var operationId = Guid.NewGuid();
        var command = new WarehouseMapCalibrationCommand(operationId, admin.Id, 0, "4826", "Calibración inicial",
            [Point("R1", "REFERENCE", 0, 0), Point("R2", "REFERENCE", 20, 0),
             Point("R3", "REFERENCE", 0, 20), Point("R4", "REFERENCE", 20, 20), Point("C1", "CHECK", 8, 11)]);

        var first = await service.PublishAsync(command);
        var repeated = await service.PublishAsync(command);
        var active = await service.GetStateAsync(includePoints: false);

        Assert.Equal(WarehouseMapCalibrationSaveStatus.Success, first.Status);
        Assert.Equal(WarehouseMapCalibrationSaveStatus.Success, repeated.Status);
        Assert.True(active.IsCurrent);
        Assert.Equal(1, await db.WarehouseMapCalibrationRevisions.CountAsync());

        (await db.WarehouseMapLayouts.SingleAsync()).Version++;
        await db.SaveChangesAsync();
        Assert.False((await service.GetStateAsync(includePoints: false)).IsCurrent);
    }

    private static WarehouseMapCalibrationPointInput Point(string name, string kind, double localX, double localY,
        double mapOffsetX = 0)
    {
        var radians = Math.PI / 180d;
        var latitude = OriginLatitude + localY / EarthRadius / radians;
        var longitude = OriginLongitude + localX / (EarthRadius * Math.Cos(OriginLatitude * radians)) / radians;
        var mapX = 2 * localX + 3 * localY + 100 + mapOffsetX;
        var mapY = -localX + 4 * localY + 50;
        var samples = Enumerable.Range(0, 3).Select(index =>
            new WarehouseMapGeoSample(latitude, longitude, 4 + index, 1000 + index)).ToArray();
        return new(Guid.NewGuid(), kind, name, (decimal)mapX, (decimal)mapY, samples);
    }

    private static WarehouseDbContext CreateDb() => new(new DbContextOptionsBuilder<WarehouseDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
