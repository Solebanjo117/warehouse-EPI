using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Labels;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Tests.Inventory;

namespace WarehouseEPI.Tests.Labels;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class PostgreSqlLabelTemplateTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Published_and_admin_template_lists_translate_on_postgresql()
    {
        await using var db = fixture.CreateDbContext();
        var service = new LabelTemplateService(
            db,
            new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey)),
            TimeProvider.System);

        var published = await service.GetPublishedAsync();
        var adminRows = await service.GetAdminRowsAsync();

        var expectedCodes = LabelTemplatePresetCatalog.RemainingExcelTemplates.Select(item => item.Code)
            .Append("LBL-6X4-ZEBRA").OrderBy(item => item).ToArray();
        var initial = Assert.Single(published, item => item.Code == "LBL-6X4-ZEBRA");
        Assert.Equal("Caja 6×4", initial.Name);
        Assert.Equal(10, published.Count);
        Assert.Equal(expectedCodes, published.Select(item => item.Code).OrderBy(item => item));
        Assert.Contains(adminRows, item => item.VersionId == initial.VersionId && item.IsCurrent);
        Assert.All(published, item => Assert.Contains(adminRows,
            admin => admin.VersionId == item.VersionId && admin.IsCurrent && admin.Status == WarehouseEPI.Core.Entities.LabelTemplateStatus.Published));
        Assert.Equal(published.OrderBy(item => item.Name).Select(item => item.VersionId), published.Select(item => item.VersionId));
        Assert.Equal(adminRows.OrderBy(item => item.Code).ThenByDescending(item => item.Version).Select(item => item.VersionId), adminRows.Select(item => item.VersionId));

        var migratedTemplateIds = LabelTemplatePresetCatalog.RemainingExcelTemplates.Select(item => item.TemplateId).ToArray();
        var events = await db.LabelTemplateEvents.Where(item => migratedTemplateIds.Contains(item.TemplateId)).ToListAsync();
        Assert.Equal(9, events.Count);
        Assert.All(events, item =>
        {
            Assert.Equal(WarehouseEPI.Core.Entities.LabelTemplateEventType.Published, item.Type);
            Assert.Null(item.RequestedByUserId);
            Assert.Contains("4X6 LABELS 2026.xlsx", item.Reason, StringComparison.Ordinal);
        });
    }
}
