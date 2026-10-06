using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Imports;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Imports;

namespace WarehouseEPI.Tests.Imports;

public sealed class WipTransferImportTests
{
    [Fact]
    public async Task Reopened_import_shows_saved_locations_and_document_even_when_source_is_empty_and_inactive()
    {
        await using var db = Db(); await db.Database.EnsureCreatedAsync();
        using var stream = Workbook(("PART", 4m, "WIP A"));
        var file = WipTransferSpreadsheetReader.Read(stream, "report.xlsx");
        var product = new Product { Sku = "PART", BaseUnitId = 1 };
        var origin = Rack("A-1-1"); origin.IsActive = false;
        var wip = new Location { Code = "M-1-1", OperationalRole = LocationOperationalRole.Wip, IsActive = false };
        var user = new User { FullName = "Importer", RoleId = 1, PinHash = "", PinLookup = "importer" };
        var movement = new InventoryMovement
        {
            OperationId = file.Rows[0].OperationId,
            RequestFingerprint = "fixture",
            Type = InventoryMovementType.Exit,
            Purpose = InventoryMovementPurpose.ProductionIssue,
            OperationalArea = wip,
            ResponsibleUser = user,
            OccurredAt = new(2026, 9, 8, 5, 0, 0, TimeSpan.Zero),
            RecordedAt = new(2026, 10, 5, 18, 0, 0, TimeSpan.Zero),
            Lines = [new() { Product = product, Quantity = 4, SourceLocation = origin }]
        };
        var document = new WipDocument
        {
            MovementLine = movement.Lines.Single(),
            Product = product,
            WipLocation = wip,
            ResponsibleUser = user,
            Quantity = 4,
            OccurredAt = movement.OccurredAt
        };
        db.AddRange(movement, document); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var review = await Service(db).ReviewAsync(file, new Dictionary<int, WipTransferResolution>(), suggestLocations: true);
        var row = Assert.Single(review.Rows);
        Assert.False(row.Pending);
        Assert.Equal(movement.Id, row.ExistingMovementId);
        Assert.Equal(document.Id, row.DocumentId);
        Assert.Equal(origin.Code, row.RecordedSource);
        Assert.Equal(wip.Code, row.RecordedDestination);
        Assert.Equal(wip.Id, row.Resolution.DestinationId);
        Assert.Equal(movement.RecordedAt, row.RecordedAt);
        Assert.Empty(row.Errors);
        Assert.Empty(review.Effects);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Product_selection_prefers_exact_sku_and_preserves_each_delivery_identity()
    {
        using var stream = Workbook(("PART", 4m, "WIP A"), ("PART-2", 9m, "WIP A"), ("PART", 4m, "WIP A"));
        var file = WipTransferSpreadsheetReader.Read(stream, "report.xlsx");
        var selected = WipTransferSelection.Select(file, " part ");
        Assert.Equal(new[] { 2, 4 }, selected.Rows.Select(x => x.Number));
        Assert.Equal(file.Hash, selected.Hash);
        var delivery = Assert.Single(WipTransferSelection.Select(file, "PART", 4).Rows);
        Assert.Equal(file.Rows[2].OperationId, delivery.OperationId);
        Assert.True(delivery.Repeated);
        Assert.Equal(3, WipTransferSelection.Select(file, "par").Rows.Count);
        Assert.Empty(WipTransferSelection.Select(file, "UNKNOWN").Rows);
        Assert.Empty(WipTransferSelection.Select(file, "PART", 3).Rows);
    }

    [Fact]
    public void Date_selection_includes_boundaries_combines_product_and_omits_undated_rows()
    {
        using var stream = Workbook(("PART", 1m, "WIP A"), ("PART", 2m, "WIP A"), ("OTHER", 3m, "WIP A"), ("PART", 4m, "WIP A"));
        var file = WipTransferSpreadsheetReader.Read(stream, "report.xlsx");
        var day = new DateOnly(2026, 9, 8);
        file = file with { Rows = file.Rows.Select((row, i) => row with { Date = i == 3 ? null : day.AddDays(i) }).ToArray() };
        Assert.Equal(new[] { 3, 4 }, WipTransferSelection.Select(file, null, from: day.AddDays(1)).Rows.Select(x => x.Number));
        Assert.Equal(new[] { 2, 3 }, WipTransferSelection.Select(file, null, to: day.AddDays(1)).Rows.Select(x => x.Number));
        var single = Assert.Single(WipTransferSelection.Select(file, "PART", from: day.AddDays(1), to: day.AddDays(2)).Rows);
        Assert.Equal(file.Rows[1].OperationId, single.OperationId);
        Assert.Empty(WipTransferSelection.Select(file, "PART", 2, from: day.AddDays(1)).Rows);
        Assert.Empty(WipTransferSelection.Select(file, null, from: day.AddDays(2), to: day).Rows);
        Assert.Equal(4, WipTransferSelection.Select(file, null).Rows.Count);
    }

    [Fact]
    public void Configured_real_transfer_report_matches_the_source_audit()
    {
        var path = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_WIP_WORKBOOK");
        if (string.IsNullOrWhiteSpace(path)) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var file = WipTransferSpreadsheetReader.Read(stream, Path.GetFileName(path));
        Assert.Equal(206, file.Rows.Count);
        Assert.Equal(204, file.Rows.Count(x => x.Quantity > 0));
        Assert.Equal(4, file.Rows.Count(x => x.Area.Length == 0));
        Assert.Equal(2, file.Rows.Count(x => x.Repeated));
        Assert.True(file.Rows.Count(x => x.Errors.Count > 0) == 2,
            string.Join(" | ", file.Rows.Take(3).Select(x => $"{x.Number}: {string.Join(';', x.Errors)}")));
    }

    [Fact]
    public void Reads_only_deliveries_and_keeps_incomplete_rows_visible()
    {
        using var stream = Workbook(("PART", 4m, "WIP A"), ("", null, ""));
        var file = WipTransferSpreadsheetReader.Read(stream, "report.xlsx");
        Assert.Equal(2, file.Rows.Count);
        Assert.Equal(4m, file.Rows[0].Quantity);
        Assert.Equal("EA", file.Rows[0].Unit);
        Assert.Equal(new DateOnly(2026, 9, 8), file.Rows[0].Date);
        Assert.Empty(file.Rows[0].Errors);
        Assert.NotEmpty(file.Rows[1].Errors);
        Assert.Equal(64, file.Hash.Length);
    }

    [Fact]
    public void Identity_survives_rename_reorder_and_overlap_but_preserves_repeated_deliveries()
    {
        using var first = Workbook(("PART", 4m, "WIP A"), ("OTHER", 2m, "WIP B"), ("PART", 4m, "WIP A"));
        using var second = Workbook(("OTHER", 2m, "WIP B"), ("PART", 4m, "WIP A"), ("PART", 4m, "WIP A"), ("NEW", 3m, "WIP A"));
        var a = WipTransferSpreadsheetReader.Read(first, "one.xlsx");
        var b = WipTransferSpreadsheetReader.Read(second, "renamed.xlsx");
        Assert.Equal(a.Rows[0].OperationId, b.Rows[1].OperationId);
        Assert.Equal(a.Rows[1].OperationId, b.Rows[0].OperationId);
        Assert.Equal(a.Rows[2].OperationId, b.Rows[2].OperationId);
        Assert.NotEqual(a.Rows[0].OperationId, a.Rows[2].OperationId);
        Assert.True(a.Rows[2].Repeated);
    }

    [Fact]
    public void Rejects_wrong_sheet_and_invalid_deliveries()
    {
        using var stream = Workbook(("PART", -4m, "WIP A"));
        using var book = new XLWorkbook(stream);
        book.Worksheet("TRANSFER LOG").Cell(2, 8).Value = "Cancelled";
        using var invalid = new MemoryStream(); book.SaveAs(invalid); invalid.Position = 0;
        Assert.Equal(2, WipTransferSpreadsheetReader.Read(invalid, "report.xlsx").Rows.Single().Errors.Count);
        book.Worksheet("TRANSFER LOG").Name = "Wrong";
        using var wrong = new MemoryStream(); book.SaveAs(wrong); wrong.Position = 0;
        Assert.Throws<InvalidDataException>(() => WipTransferSpreadsheetReader.Read(wrong, "report.xlsx"));
    }

    [Fact]
    public async Task Single_source_is_prefilled_multiple_sources_require_choice_and_manual_choices_survive()
    {
        await using var db = Db();
        await db.Database.EnsureCreatedAsync();
        var product = new Product { Sku = "PART", BaseUnitId = 1 };
        var first = Rack("A-1-1");
        var second = Rack("A-1-2");
        var blocked = Rack("BLOCKED"); blocked.IsBlocked = true;
        var wip = new Location { Code = "WIP A", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        db.Products.Add(product); db.Locations.AddRange(first, second, blocked, wip);
        db.InventoryBalances.Add(new() { ProductId = product.Id, LocationId = first.Id, Quantity = 2m });
        db.ProductLocationAssignments.AddRange(new ProductLocationAssignment { ProductId = product.Id, LocationId = first.Id },
            new ProductLocationAssignment { ProductId = product.Id, LocationId = blocked.Id },
            new ProductLocationAssignment { ProductId = product.Id, LocationId = wip.Id });
        await db.SaveChangesAsync();
        using var stream = Workbook(("PART", 4m, "WIP A"));
        var file = WipTransferSpreadsheetReader.Read(stream, "report.xlsx");
        var importer = Service(db);
        var initial = await importer.ReviewAsync(file, new Dictionary<int, WipTransferResolution>(), suggestLocations: true);
        Assert.Equal(first.Id, initial.Rows[0].Resolution.SourceId);
        Assert.True(initial.Rows[0].Resolution.AutomaticSource);
        Assert.Equal(wip.Id, initial.Rows[0].Resolution.DestinationId);
        Assert.True(initial.CanConfirm);
        Assert.Equal(-2m, initial.Effects.Single(x => x.Location == first.Code).After);
        Assert.Empty(db.InventoryMovements);
        db.ProductLocationAssignments.Add(new() { ProductId = product.Id, LocationId = second.Id });
        db.InventoryBalances.Add(new() { ProductId = product.Id, LocationId = second.Id, Quantity = 5m });
        await db.SaveChangesAsync();
        var multiple = await importer.ReviewAsync(file, new Dictionary<int, WipTransferResolution>(), suggestLocations: true);
        Assert.Null(multiple.Rows[0].Resolution.SourceId);
        Assert.Equal(2, multiple.Rows[0].SourceOptions.Count);
        Assert.False(multiple.CanConfirm);
        var manual = await importer.ReviewAsync(file, new Dictionary<int, WipTransferResolution> { [2] = new(second.Id, wip.Id) }, suggestLocations: true);
        Assert.Equal(second.Id, manual.Rows[0].Resolution.SourceId);
        Assert.False(manual.Rows[0].Resolution.AutomaticSource);
        Assert.True(manual.CanConfirm);
    }

    [Fact]
    public async Task Balance_only_source_is_prefilled_but_unavailable_and_zero_unassigned_locations_are_ignored()
    {
        await using var db = Db(); await db.Database.EnsureCreatedAsync();
        var product = new Product { Sku = "PART", BaseUnitId = 1 };
        var source = Rack("A-1-1");
        var unavailable = Rack("A-1-2"); unavailable.IsPhysicallyPresent = false;
        var zero = Rack("A-1-3");
        var negative = Rack("NEGATIVE");
        var wipRack = Rack("WIP-RACK"); wipRack.OperationalRole = LocationOperationalRole.Wip;
        var otherRack = Rack("OTHER");
        var otherProduct = new Product { Sku = "OTHER", BaseUnitId = 1 };
        db.Products.Add(otherProduct);
        db.Locations.AddRange(negative, wipRack, otherRack);
        db.ProductLocationAssignments.Add(new() { ProductId = product.Id, LocationId = zero.Id });
        db.InventoryBalances.AddRange(new InventoryBalance { ProductId = product.Id, LocationId = negative.Id, Quantity = -2m },
            new InventoryBalance { ProductId = product.Id, LocationId = wipRack.Id, Quantity = 10m },
            new InventoryBalance { ProductId = otherProduct.Id, LocationId = otherRack.Id, Quantity = 15m });
        db.Products.Add(product); db.Locations.AddRange(source, unavailable, zero);
        db.InventoryBalances.AddRange(new InventoryBalance { ProductId = product.Id, LocationId = source.Id, Quantity = 8m },
            new InventoryBalance { ProductId = product.Id, LocationId = unavailable.Id, Quantity = 20m },
            new InventoryBalance { ProductId = product.Id, LocationId = zero.Id, Quantity = 0m });
        await db.SaveChangesAsync();
        using var stream = Workbook(("PART", 4m, "WIP A"));
        var review = await Service(db).ReviewAsync(WipTransferSpreadsheetReader.Read(stream, "report.xlsx"),
            new Dictionary<int, WipTransferResolution>(), suggestLocations: true);
        Assert.Equal(source.Id, review.Rows.Single().Resolution.SourceId);
        Assert.Equal(8m, Assert.Single(review.Rows.Single().SourceOptions).Quantity);
        foreach (var invalid in new[] { zero, negative, wipRack, otherRack, unavailable })
        {
            stream.Position = 0;
            var invalidReview = await Service(db).ReviewAsync(WipTransferSpreadsheetReader.Read(stream, "report.xlsx"),
                new Dictionary<int, WipTransferResolution> { [2] = new(invalid.Id) });
            Assert.Contains(invalidReview.Rows.Single().Errors, error => error.Contains("stock positivo", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Unknown_product_unit_mismatch_and_missing_destination_block_confirmation()
    {
        await using var db = Db(); await db.Database.EnsureCreatedAsync();
        db.Products.Add(new() { Sku = "PART", BaseUnitId = 2 }); await db.SaveChangesAsync();
        using var stream = Workbook(("PART", 4m, ""), ("MISSING", 3m, "Unknown"));
        var file = WipTransferSpreadsheetReader.Read(stream, "report.xlsx");
        var review = await Service(db).ReviewAsync(file, new Dictionary<int, WipTransferResolution>(), suggestLocations: true);
        Assert.False(review.CanConfirm);
        Assert.Contains(review.Rows[0].Errors, x => x.Contains("unidad", StringComparison.Ordinal));
        Assert.All(review.Rows, row => Assert.Contains(row.Errors, x => x.Contains("área WIP", StringComparison.Ordinal)));
        Assert.Empty(db.InventoryMovements);
    }

    [Fact]
    public void Preview_is_bound_to_owner_and_revisions_do_not_mutate_old_snapshots()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var store = new WipTransferPreviewStore(cache, TimeProvider.System);
        using var stream = Workbook(("PART", 4m, "WIP A"));
        var draft = store.Create(Guid.NewGuid(), WipTransferSpreadsheetReader.Read(stream, "report.xlsx"), new Dictionary<int, WipTransferResolution>());
        Assert.Null(store.Get(draft.Token, Guid.NewGuid()));
        store.Save(draft with { Revision = 2 });
        Assert.Equal(1, draft.Revision);
        Assert.Equal(2, store.Get(draft.Token, draft.OwnerId)!.Revision);
    }

    internal static WarehouseDbContext Db() => new(new DbContextOptionsBuilder<WarehouseDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    internal static Location Rack(string code) => new() { Code = code, Kind = LocationKind.Rack };
    internal static UserPinService Pins(WarehouseDbContext db) => new(db,
        new PinProtector("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="));
    internal static WipTransferImportService Service(WarehouseDbContext db, ILogger<WipTransferImportService>? logger = null)
    {
        var pins = Pins(db);
        var clock = new WarehouseClock(new WarehouseSettingsService(db));
        return new(db, new InventoryMovementService(db, pins, TimeProvider.System, clock), pins, clock, TimeProvider.System, logger);
    }
    internal static MemoryStream Workbook(params (string Sku, decimal? Quantity, string Area)[] rows)
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("TRANSFER LOG");
        string[] headers = ["Transfer Date", "Part Number", "Description", "UOM", "Qty Requested", "Qty Delivered", "Delivery WIP Location", "Status"];
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        for (var i = 0; i < rows.Length; i++)
        {
            sheet.Cell(i + 2, 1).Value = new DateTime(2026, 9, 8);
            sheet.Cell(i + 2, 2).Value = rows[i].Sku;
            sheet.Cell(i + 2, 4).Value = "Each (EA)";
            sheet.Cell(i + 2, 5).Value = 9999;
            if (rows[i].Quantity is { } qty) sheet.Cell(i + 2, 6).Value = qty;
            sheet.Cell(i + 2, 7).Value = rows[i].Area;
        }
        book.AddWorksheet("WIP Transfer Report").Cell(1, 1).Value = 9999;
        var stream = new MemoryStream(); book.SaveAs(stream); stream.Position = 0; return stream;
    }
}
