using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Reporting;

namespace WarehouseEPI.Tests.Reporting;

public sealed class WipSummaryTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Summary_filters_effective_applications_units_and_full_population()
    {
        await using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await db.Database.EnsureCreatedAsync();
        await SeedAndVerifyAsync(db);
    }

    [Theory]
    [InlineData(null, null, null, "newest")]
    [InlineData("pending", null, "pending", "oldest")]
    [InlineData("AGED", "newest", "aged", "newest")]
    [InlineData("invalid", "invalid", null, "newest")]
    public void Selection_preserves_compatible_defaults(string? attention, string? sort, string? expected, string order)
    {
        var selection = WipReportSelection.FromQuery(attention, sort);
        Assert.Equal(expected, selection.Attention);
        Assert.Equal(order, selection.Sort);
        var filter = selection.Apply(new(null, null), Now, 7);
        Assert.Equal(expected is not null, filter.PendingOnly);
        Assert.Equal(expected == "aged" ? Now.AddDays(-7) : (DateTimeOffset?)null, filter.AgedBefore);
    }

    internal static async Task SeedAndVerifyAsync(WarehouseDbContext db)
    {
        var user = new User { FullName = "Summary test", RoleId = 1, PinHash = "test", PinLookup = Guid.NewGuid().ToString() };
        var area = new Location { Code = "SUMMARY-WIP", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        var otherArea = new Location { Code = "OTHER-WIP", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        var ea = new Product { Sku = "SUM-EA", BaseUnitId = 1 };
        var kg = new Product { Sku = "SUM-KG", BaseUnitId = 2 };
        var unknown1 = new Product { Sku = "SUM-U1", BaseUnitId = 18 };
        var unknown2 = new Product { Sku = "SUM-U2", BaseUnitId = 18 };
        db.AddRange(user, area, otherArea, ea, kg, unknown1, unknown2);
        WipDocument Document(Product product, decimal quantity, DateTimeOffset at) => new()
        {
            Product = product, WipLocation = area, ResponsibleUser = user, Quantity = quantity, OccurredAt = at, IsOpening = true
        };
        var boundary = Document(ea, 20, Now.AddDays(-7));
        var reversed = new WipDocumentApplication { Document = boundary, Kind = WipDocumentApplicationKind.Consumption,
            Quantity = 8, ResponsibleUserId = user.Id, OperationId = Guid.NewGuid(), RecordedAt = Now };
        boundary.Applications.Add(reversed);
        boundary.Applications.Add(new() { Document = boundary, Kind = WipDocumentApplicationKind.Reversal, Quantity = 8,
            ReversesApplicationId = reversed.Id, ResponsibleUserId = user.Id, OperationId = Guid.NewGuid(), RecordedAt = Now });
        foreach (var kind in new[] { WipDocumentApplicationKind.Consumption, WipDocumentApplicationKind.Scrap,
            WipDocumentApplicationKind.WarehouseReturn, WipDocumentApplicationKind.SupplierReturn })
            boundary.Applications.Add(new() { Document = boundary, Kind = kind, Quantity = 1,
                ResponsibleUserId = user.Id, OperationId = Guid.NewGuid(), RecordedAt = Now });
        var closed = Document(ea, 5, Now.AddDays(-30));
        closed.Applications.Add(new() { Document = closed, Kind = WipDocumentApplicationKind.Consumption, Quantity = 5,
            ResponsibleUserId = user.Id, OperationId = Guid.NewGuid(), RecordedAt = Now });
        var cancelled = Document(ea, 999, Now.AddDays(-40)); cancelled.IsCancelled = true;
        var elsewhere = Document(ea, 999, Now.AddDays(-40)); elsewhere.WipLocation = otherArea;
        db.AddRange(boundary, closed, cancelled, elsewhere, Document(kg, 3, Now.AddDays(-8)),
            Document(unknown1, 4, Now.AddDays(-8)), Document(unknown2, 6, Now.AddDays(-8)));
        for (var i = 0; i < 26; i++) db.Add(Document(ea, 1, Now.AddDays(-7).AddSeconds(i + 1)));
        await db.SaveChangesAsync();
        var settings = new WarehouseSettingsService(db);
        var service = new WipReportService(db, new WarehouseClock(settings));
        var filter = new WipReportFilter(null, null, "SUM-", area.Id);
        var all = await service.GetDocumentSummaryAsync(filter, Now.AddDays(-7));
        Assert.Equal(30, all.PendingDocuments);
        Assert.Equal(4, all.AgedDocuments);
        Assert.Equal(42, all.Units.Single(x => x.UnitId == 1).Quantity);
        Assert.Equal(3, all.Units.Single(x => x.UnitId == 2).Quantity);
        Assert.Equal(2, all.Units.Count(x => x.UnitId == 18));
        Assert.Equal(new[] { "SUM-U1", "SUM-U2" }, all.Units.Where(x => x.UnitId == 18).Select(x => x.Sku));
        var pending = filter with { PendingOnly = true, OldestFirst = true };
        var first = await service.GetTrackedPageAsync(pending, 1, 25);
        var second = await service.GetTrackedPageAsync(pending, 2, 25);
        Assert.Equal(30, first.TotalActivityCount);
        Assert.Equal(25, first.Inventory.Count);
        Assert.Equal(5, second.Inventory.Count);
        Assert.Empty(first.Inventory.Select(x => x.DocumentId).Intersect(second.Inventory.Select(x => x.DocumentId)));
        foreach (var unit in all.Units)
            Assert.Equal(unit.Quantity, first.Inventory.Concat(second.Inventory)
                .Where(x => x.Unit == unit.Unit && (unit.ProductId is null || x.ProductId == unit.ProductId))
                .Sum(x => x.Quantity));
        var aged = pending with { AgedBefore = Now.AddDays(-7) };
        var old = await service.GetDocumentSummaryAsync(aged, Now.AddDays(-7));
        Assert.Equal(30, old.PendingDocuments);
        Assert.Equal(4, old.AgedDocuments);
        Assert.Equal(16, old.Units.Single(x => x.UnitId == 1).Quantity);
        var oldPage = await service.GetTrackedPageAsync(aged, 1, 25);
        Assert.Equal(4, oldPage.TotalActivityCount);
        Assert.Equal(16, oldPage.Inventory.Single(x => x.DocumentId == boundary.Id).Quantity);
        Assert.Equal(boundary.Id, oldPage.Inventory.Last().DocumentId);
        var newest = await service.GetTrackedPageAsync(filter, 1, 25);
        Assert.True(newest.Inventory.First().UpdatedAt > first.Inventory.First().UpdatedAt);
        var dates = filter with { From = Now.AddDays(-7), To = Now.AddDays(-7).AddSeconds(1) };
        Assert.Equal(1, (await service.GetDocumentSummaryAsync(dates, Now.AddDays(-7))).PendingDocuments);
        var empty = await service.GetDocumentSummaryAsync(filter with { Search = "NO-MATCH" }, Now.AddDays(-7));
        Assert.Equal(0, empty.PendingDocuments);
        Assert.Empty(empty.Units);
    }
}
