using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Reporting;

public sealed class UnifiedTraceServiceTests
{
    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new WarehouseDbContext(options);
    }

    [Fact]
    public async Task Unified_trace_searches_and_combines_multiple_sources_chronologically()
    {
        await using var db = CreateDbContext();
        var user = new User { Id = Guid.NewGuid(), FullName = "Auditor Jefe", PinLookup = "lk", PinHash = "ph", RoleId = 1 };
        var product = new Product { Id = Guid.NewGuid(), Sku = "SKU-TRACE-01", BaseUnitId = 1 };
        var unit = new Unit { Id = 1, Code = "PZA", Name = "Pieza" };
        var srcLoc = new Location { Id = Guid.NewGuid(), Code = "SRC-01", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        var dstLoc = new Location { Id = Guid.NewGuid(), Code = "DST-02", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        var areaLoc = new Location { Id = Guid.NewGuid(), Code = "WIP-AREA", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        db.AddRange(user, product, unit, srcLoc, dstLoc, areaLoc);

        var baseTime = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

        // 1. Movement: Entry at baseTime + 1h
        var mov = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Type = InventoryMovementType.Transfer,
            Purpose = InventoryMovementPurpose.Standard,
            ResponsibleUser = user,
            OccurredAt = baseTime.AddHours(1),
            RequestFingerprint = "fp-mov"
        };
        var movLine = new InventoryMovementLine
        {
            Id = Guid.NewGuid(),
            Movement = mov,
            Product = product,
            Unit = unit,
            SourceLocation = srcLoc,
            DestinationLocation = dstLoc,
            Quantity = 15m,
            LineNumber = 1
        };
        db.InventoryMovements.Add(mov);
        db.InventoryMovementLines.Add(movLine);

        // 2. Receiving document event at baseTime + 2h
        var doc = new ReceivingDocument
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Number = "DOC-REC-100",
            NormalizedNumber = "DOC-REC-100",
            Origin = "PROV-ACME",
            NormalizedOrigin = "PROV-ACME",
            Status = ReceivingDocumentStatus.Open,
            OpenedByUserId = user.Id,
            RequestFingerprint = "fp-doc"
        };
        var docEvent = new ReceivingDocumentEvent
        {
            Id = Guid.NewGuid(),
            ReceivingDocument = doc,
            Type = ReceivingDocumentEventType.Opened,
            ActorUserId = user.Id,
            ActorUser = user,
            RecordedAt = baseTime.AddHours(2)
        };
        db.ReceivingDocuments.Add(doc);
        db.ReceivingDocumentEvents.Add(docEvent);

        // 3. WIP disposition at baseTime + 3h
        var wip = new WipDisposition
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            OriginalMovementLine = movLine,
            Type = WipDispositionType.WarehouseReturn,
            Quantity = 5m,
            DestinationLocation = dstLoc,
            ResponsibleUser = user,
            OccurredAt = baseTime.AddHours(3),
            RequestFingerprint = "fp-wip"
        };
        db.WipDispositions.Add(wip);

        // 4. Cycle count action at baseTime + 4h
        var campaign = new CycleCountCampaign
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Number = 42,
            Title = "Campaña General",
            CreatedByUserId = user.Id,
            Status = CycleCountCampaignStatus.InProgress
        };
        var countAction = new CycleCountAction
        {
            Id = Guid.NewGuid(),
            Campaign = campaign,
            Type = CycleCountActionType.Created,
            ResponsibleUser = user,
            RecordedAt = baseTime.AddHours(4)
        };
        db.CycleCountCampaigns.Add(campaign);
        db.CycleCountActions.Add(countAction);

        await db.SaveChangesAsync();

        var service = new UnifiedTraceService(db);

        // Search all: should return 4 items, sorted desc by OccurredAt
        var all = await service.SearchAsync(new(null, null, null, null, 1, 25));
        Assert.Equal(4, all.TotalCount);
        Assert.Equal(4, all.Items.Count);
        Assert.Equal("Conteo", all.Items[0].Kind);       // baseTime + 4h
        Assert.Equal("WIP", all.Items[1].Kind);          // baseTime + 3h
        Assert.Equal("Documento", all.Items[2].Kind);    // baseTime + 2h
        Assert.Equal("Movimiento", all.Items[3].Kind);   // baseTime + 1h

        // Verify transfer route shows Source -> Destination
        Assert.Equal("SRC-01 → DST-02", all.Items[3].Location);

        // Search by kind: movement
        var movOnly = await service.SearchAsync(new(null, null, null, "movement", 1, 25));
        Assert.Equal(1, movOnly.TotalCount);
        Assert.Equal("Movimiento", movOnly.Items[0].Kind);

        // Search by kind: wip
        var wipOnly = await service.SearchAsync(new(null, null, null, "wip", 1, 25));
        Assert.Equal(1, wipOnly.TotalCount);
        Assert.Equal("WIP", wipOnly.Items[0].Kind);

        // Search by text: SKU
        var bySku = await service.SearchAsync(new(null, null, "SKU-TRACE-01", null, 1, 25));
        Assert.True(bySku.TotalCount >= 1);
        Assert.All(bySku.Items, item => Assert.True(item.ProductSku == "SKU-TRACE-01" || item.Summary.Contains("SKU-TRACE-01")));

        // Export validation with UnifiedTraceExportService
        var settingsService = new WarehouseSettingsService(db);
        var exportService = new UnifiedTraceExportService(settingsService);

        var filter = new UnifiedTraceFilter(null, null, null, null, 1, 10000);
        var exportBatch = await service.ExportAsync(filter, 10000);
        Assert.Equal(4, exportBatch.TotalRows);

        var excelBytes = await exportService.ToExcelAsync(exportBatch.Items, filter);
        Assert.NotNull(excelBytes);
        Assert.True(excelBytes.Length > 0);
        using var stream = new MemoryStream(excelBytes);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet("Trazabilidad");
        Assert.Equal("Conteo", sheet.Cell(5, 3).GetString());
        Assert.Equal("Movimiento", sheet.Cell(8, 3).GetString());

        var csvBytes = await exportService.ToCsvAsync(exportBatch.Items, filter);
        var csvText = Encoding.UTF8.GetString(csvBytes);
        Assert.Contains("SKU-TRACE-01", csvText);
        Assert.Contains("SRC-01 → DST-02", csvText);
    }
}
