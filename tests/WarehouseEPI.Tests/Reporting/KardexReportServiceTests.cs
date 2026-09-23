using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Reporting;

public sealed class KardexReportServiceTests
{
    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new WarehouseDbContext(options);
    }

    [Fact]
    public async Task Kardex_reconstructs_initial_balance_backwards_and_preserves_arithmetic_continuity()
    {
        await using var db = CreateDbContext();
        var user = new User { Id = Guid.NewGuid(), FullName = "Almacenista", PinLookup = "lk", PinHash = "ph", RoleId = 1 };
        var product = new Product { Id = Guid.NewGuid(), Sku = "SKU-KARDEX-01", BaseUnitId = 1 };
        var unit = new Unit { Id = 1, Code = "PZA", Name = "Pieza" };
        var loc = new Location { Id = Guid.NewGuid(), Code = "RACK-01", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        db.AddRange(user, product, unit, loc);

        var day1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero);
        var day3 = new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero);

        // Day 1: Entry +50
        var mov1 = new InventoryMovement { Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Type = InventoryMovementType.Entry, Purpose = InventoryMovementPurpose.DocumentReceipt, ResponsibleUser = user, OccurredAt = day1, RequestFingerprint = "fp1" };
        var line1 = new InventoryMovementLine { Id = Guid.NewGuid(), Movement = mov1, Product = product, Unit = unit, DestinationLocation = loc, Quantity = 50m, LineNumber = 1 };
        line1.BalanceChanges.Add(new InventoryBalanceChange { Id = Guid.NewGuid(), MovementLine = line1, Location = loc, DeltaQuantity = 50m, PreviousQuantity = 0m, ResultingQuantity = 50m });
        db.InventoryMovements.Add(mov1);
        db.InventoryMovementLines.Add(line1);

        // Day 2: Exit -20
        var mov2 = new InventoryMovement { Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Type = InventoryMovementType.Exit, Purpose = InventoryMovementPurpose.ProductionIssue, ResponsibleUser = user, OccurredAt = day2, RequestFingerprint = "fp2" };
        var line2 = new InventoryMovementLine { Id = Guid.NewGuid(), Movement = mov2, Product = product, Unit = unit, SourceLocation = loc, Quantity = 20m, LineNumber = 1 };
        line2.BalanceChanges.Add(new InventoryBalanceChange { Id = Guid.NewGuid(), MovementLine = line2, Location = loc, DeltaQuantity = -20m, PreviousQuantity = 50m, ResultingQuantity = 30m });
        db.InventoryMovements.Add(mov2);
        db.InventoryMovementLines.Add(line2);

        // Day 3: Entry +70
        var mov3 = new InventoryMovement { Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Type = InventoryMovementType.Entry, Purpose = InventoryMovementPurpose.Standard, ResponsibleUser = user, OccurredAt = day3, RequestFingerprint = "fp3" };
        var line3 = new InventoryMovementLine { Id = Guid.NewGuid(), Movement = mov3, Product = product, Unit = unit, DestinationLocation = loc, Quantity = 70m, LineNumber = 1 };
        line3.BalanceChanges.Add(new InventoryBalanceChange { Id = Guid.NewGuid(), MovementLine = line3, Location = loc, DeltaQuantity = 70m, PreviousQuantity = 30m, ResultingQuantity = 100m });
        db.InventoryMovements.Add(mov3);
        db.InventoryMovementLines.Add(line3);

        // Physical balance in table = 100
        db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = product.Id, LocationId = loc.Id, Quantity = 100m });
        await db.SaveChangesAsync();

        var service = new KardexReportService(db);

        // Query starting from Day 2:
        // Expected Initial Balance: Current (100) - DeltasFromDay2 (-20 + 70 = 50) = 50
        var kardex = await service.GetKardexAsync(new KardexFilter(product.Id, FromUtc: day2));
        Assert.NotNull(kardex);

        Assert.Equal(50m, kardex.Summary.InitialBalance);
        Assert.Equal(100m, kardex.Summary.CurrentPhysicalBalance);
        Assert.Equal(2, kardex.Rows.Count);

        // Row 1: Exit -20
        Assert.Equal(day2, kardex.Rows[0].OccurredAt);
        Assert.Equal(0m, kardex.Rows[0].EntryQuantity);
        Assert.Equal(20m, kardex.Rows[0].ExitQuantity);
        Assert.Equal(-20m, kardex.Rows[0].NetDelta);
        Assert.Equal(30m, kardex.Rows[0].RunningBalance); // 50 - 20 = 30

        // Row 2: Entry +70
        Assert.Equal(day3, kardex.Rows[1].OccurredAt);
        Assert.Equal(70m, kardex.Rows[1].EntryQuantity);
        Assert.Equal(0m, kardex.Rows[1].ExitQuantity);
        Assert.Equal(70m, kardex.Rows[1].NetDelta);
        Assert.Equal(100m, kardex.Rows[1].RunningBalance); // 30 + 70 = 100

        Assert.Equal(100m, kardex.Summary.EndingBalance);
        Assert.Equal(70m, kardex.Summary.TotalEntries);
        Assert.Equal(20m, kardex.Summary.TotalExits);

        // Verify fundamental equation: Initial + Entries - Exits = Ending
        Assert.Equal(kardex.Summary.EndingBalance, kardex.Summary.InitialBalance + kardex.Summary.TotalEntries - kardex.Summary.TotalExits);
    }

    [Fact]
    public async Task Kardex_transfer_is_neutral_globally_and_impacts_individual_locations()
    {
        await using var db = CreateDbContext();
        var user = new User { Id = Guid.NewGuid(), FullName = "Montacarguista", PinLookup = "lk", PinHash = "ph", RoleId = 1 };
        var product = new Product { Id = Guid.NewGuid(), Sku = "SKU-TRANSFER-01", BaseUnitId = 1 };
        var unit = new Unit { Id = 1, Code = "PZA", Name = "Pieza" };
        var locSrc = new Location { Id = Guid.NewGuid(), Code = "RACK-SRC", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        var locDst = new Location { Id = Guid.NewGuid(), Code = "RACK-DST", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        db.AddRange(user, product, unit, locSrc, locDst);

        var t0 = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
        var tTransfer = new DateTimeOffset(2026, 8, 5, 14, 0, 0, TimeSpan.Zero);

        // Initial entry to SRC: 50
        var movEntry = new InventoryMovement { Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Type = InventoryMovementType.Entry, Purpose = InventoryMovementPurpose.Standard, ResponsibleUser = user, OccurredAt = t0, RequestFingerprint = "fp-entry" };
        var lineEntry = new InventoryMovementLine { Id = Guid.NewGuid(), Movement = movEntry, Product = product, Unit = unit, DestinationLocation = locSrc, Quantity = 50m, LineNumber = 1 };
        lineEntry.BalanceChanges.Add(new InventoryBalanceChange { Id = Guid.NewGuid(), MovementLine = lineEntry, Location = locSrc, DeltaQuantity = 50m, PreviousQuantity = 0m, ResultingQuantity = 50m });
        db.InventoryMovements.Add(movEntry);
        db.InventoryMovementLines.Add(lineEntry);

        // Transfer 15 from SRC to DST
        var movTransfer = new InventoryMovement { Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Type = InventoryMovementType.Transfer, Purpose = InventoryMovementPurpose.Standard, ResponsibleUser = user, OccurredAt = tTransfer, RequestFingerprint = "fp-trf" };
        var lineTransfer = new InventoryMovementLine { Id = Guid.NewGuid(), Movement = movTransfer, Product = product, Unit = unit, SourceLocation = locSrc, DestinationLocation = locDst, Quantity = 15m, LineNumber = 1 };
        lineTransfer.BalanceChanges.Add(new InventoryBalanceChange { Id = Guid.NewGuid(), MovementLine = lineTransfer, Location = locSrc, DeltaQuantity = -15m, PreviousQuantity = 50m, ResultingQuantity = 35m });
        lineTransfer.BalanceChanges.Add(new InventoryBalanceChange { Id = Guid.NewGuid(), MovementLine = lineTransfer, Location = locDst, DeltaQuantity = 15m, PreviousQuantity = 0m, ResultingQuantity = 15m });
        db.InventoryMovements.Add(movTransfer);
        db.InventoryMovementLines.Add(lineTransfer);

        // Physical balances: SRC = 35, DST = 15 (Total = 50)
        db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = product.Id, LocationId = locSrc.Id, Quantity = 35m });
        db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = product.Id, LocationId = locDst.Id, Quantity = 15m });
        await db.SaveChangesAsync();

        var service = new KardexReportService(db);

        // 1. GLOBAL VIEW:
        var globalKardex = await service.GetKardexAsync(new KardexFilter(product.Id));
        Assert.NotNull(globalKardex);
        Assert.Equal(50m, globalKardex.Summary.CurrentPhysicalBalance);
        Assert.Equal(2, globalKardex.Rows.Count);

        var transferRowGlobal = globalKardex.Rows[1];
        Assert.Equal(InventoryMovementType.Transfer, transferRowGlobal.Type);
        Assert.Equal("RACK-SRC → RACK-DST", transferRowGlobal.RouteOrLocation);
        Assert.Equal(15m, transferRowGlobal.Quantity);
        Assert.Equal(0m, transferRowGlobal.EntryQuantity);
        Assert.Equal(0m, transferRowGlobal.ExitQuantity);
        Assert.Equal(0m, transferRowGlobal.NetDelta); // Neutral globally!
        Assert.Equal(50m, transferRowGlobal.RunningBalance); // Balance remains 50
        Assert.Equal(15m, globalKardex.Summary.TotalTransfers);

        // 2. LOCATION SRC VIEW:
        var srcKardex = await service.GetKardexAsync(new KardexFilter(product.Id, LocationId: locSrc.Id));
        Assert.NotNull(srcKardex);
        Assert.Equal(35m, srcKardex.Summary.CurrentPhysicalBalance);
        Assert.Equal(2, srcKardex.Rows.Count);

        var transferRowSrc = srcKardex.Rows[1];
        Assert.Equal(0m, transferRowSrc.EntryQuantity);
        Assert.Equal(15m, transferRowSrc.ExitQuantity);
        Assert.Equal(-15m, transferRowSrc.NetDelta);
        Assert.Equal(35m, transferRowSrc.RunningBalance); // 50 - 15 = 35

        // 3. LOCATION DST VIEW:
        var dstKardex = await service.GetKardexAsync(new KardexFilter(product.Id, LocationId: locDst.Id));
        Assert.NotNull(dstKardex);
        Assert.Equal(15m, dstKardex.Summary.CurrentPhysicalBalance);
        Assert.Single(dstKardex.Rows); // Only transfer touched DST

        var transferRowDst = dstKardex.Rows[0];
        Assert.Equal(15m, transferRowDst.EntryQuantity);
        Assert.Equal(0m, transferRowDst.ExitQuantity);
        Assert.Equal(15m, transferRowDst.NetDelta);
        Assert.Equal(15m, transferRowDst.RunningBalance); // 0 + 15 = 15
    }

    [Fact]
    public async Task Kardex_splits_one_fifo_line_by_historical_lot_snapshots()
    {
        await using var db = CreateDbContext();
        var user = new User { FullName = "Operador", PinLookup = "lk", PinHash = "ph", RoleId = 1 };
        var unit = new Unit { Id = 1, Code = "PZA", Name = "Pieza" };
        var product = new Product { Sku = "SKU-MULTILOTE", BaseUnitId = 1, BaseUnit = unit };
        var location = new Location { Code = "A-1-1", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        var movement = new InventoryMovement { Type = InventoryMovementType.Exit, Purpose = InventoryMovementPurpose.GeneralExit, ResponsibleUser = user, OccurredAt = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero), OperationId = Guid.NewGuid(), RequestFingerprint = "multi" };
        var line = new InventoryMovementLine { Movement = movement, Product = product, Unit = unit, SourceLocation = location, Quantity = 15m, LineNumber = 1 };
        line.BalanceChanges.Add(new InventoryBalanceChange { MovementLine = line, Location = location, LotId = Guid.NewGuid(), LotNumberSnapshot = "AUTO-20260801", DeltaQuantity = -10m, PreviousQuantity = 10m, ResultingQuantity = 0m });
        line.BalanceChanges.Add(new InventoryBalanceChange { MovementLine = line, Location = location, LotId = Guid.NewGuid(), LotNumberSnapshot = "AUTO-20260802", DeltaQuantity = -5m, PreviousQuantity = 10m, ResultingQuantity = 5m });
        db.AddRange(user, unit, product, location, movement, line);
        db.InventoryBalances.Add(new InventoryBalance { Product = product, Location = location, Quantity = 5m });
        await db.SaveChangesAsync();

        var result = await new KardexReportService(db).GetKardexAsync(new KardexFilter(product.Id));

        Assert.NotNull(result);
        Assert.Equal(20m, result.Summary.InitialBalance);
        Assert.Collection(result.Rows,
            first => { Assert.Equal("AUTO-20260801", first.LotNumber); Assert.Equal(10m, first.ExitQuantity); Assert.Equal(10m, first.RunningBalance); },
            second => { Assert.Equal("AUTO-20260802", second.LotNumber); Assert.Equal(5m, second.ExitQuantity); Assert.Equal(5m, second.RunningBalance); });

        var service = new KardexReportService(db);
        var exactLimit = await service.GetKardexExportAsync(new KardexFilter(product.Id), maximumRows: 2);
        Assert.Equal(2, exactLimit!.Rows.Count);
        await Assert.ThrowsAsync<KardexExportLimitExceededException>(() =>
            service.GetKardexExportAsync(new KardexFilter(product.Id), maximumRows: 1));
    }

    [Fact]
    public async Task Kardex_empty_export_has_numeric_zero_totals_without_circular_formulas_and_escapes_metadata()
    {
        await using var db = CreateDbContext();
        var unit = new Unit { Id = 1, Code = "PZA", Name = "Pieza" };
        var product = new Product { Sku = "SKU-CSV", Description = "Producto,\r\n\"especial\"", BaseUnitId = 1, BaseUnit = unit };
        db.AddRange(unit, product);
        await db.SaveChangesAsync();
        var result = await new KardexReportService(db).GetKardexAsync(new KardexFilter(product.Id));
        Assert.NotNull(result);
        var export = new KardexExportService(new WarehouseSettingsService(db));

        using var workbook = new XLWorkbook(new MemoryStream(await export.ToExcelAsync(result, DateTimeOffset.UtcNow)));
        var sheet = workbook.Worksheet("Kardex");
        Assert.False(sheet.Cell(7, 10).HasFormula);
        Assert.False(sheet.Cell(7, 11).HasFormula);
        Assert.Equal(0m, sheet.Cell(7, 10).GetValue<decimal>());
        Assert.Equal(0m, sheet.Cell(7, 11).GetValue<decimal>());

        var csv = Encoding.UTF8.GetString(await export.ToCsvAsync(result, DateTimeOffset.UtcNow));
        using var parser = new TextFieldParser(new StringReader(csv)) { HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        var metadata = Assert.Single(parser.ReadFields()!);
        Assert.Equal("# Kardex de Producto: SKU-CSV - Producto,\r\n\"especial\"", metadata.TrimStart('\uFEFF'));
    }

    [Fact]
    public async Task Kardex_displays_corrections_and_reversals_with_arithmetic_integrity()
    {
        await using var db = CreateDbContext();
        var user = new User { Id = Guid.NewGuid(), FullName = "Supervisor", PinLookup = "lk", PinHash = "ph", RoleId = 1 };
        var product = new Product { Id = Guid.NewGuid(), Sku = "SKU-CORR-01", BaseUnitId = 1 };
        var unit = new Unit { Id = 1, Code = "PZA", Name = "Pieza" };
        var loc = new Location { Id = Guid.NewGuid(), Code = "RACK-CORR", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        db.AddRange(user, product, unit, loc);

        var t1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero);

        // Mov1: Original Entry of 40
        var movOriginal = new InventoryMovement { Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Type = InventoryMovementType.Entry, Purpose = InventoryMovementPurpose.Standard, ResponsibleUser = user, OccurredAt = t1, RequestFingerprint = "fp-orig" };
        var lineOriginal = new InventoryMovementLine { Id = Guid.NewGuid(), Movement = movOriginal, Product = product, Unit = unit, DestinationLocation = loc, Quantity = 40m, LineNumber = 1 };
        lineOriginal.BalanceChanges.Add(new InventoryBalanceChange { Id = Guid.NewGuid(), MovementLine = lineOriginal, Location = loc, DeltaQuantity = 40m, PreviousQuantity = 0m, ResultingQuantity = 40m });
        db.InventoryMovements.Add(movOriginal);
        db.InventoryMovementLines.Add(lineOriginal);

        // Mov2: Reversal of 40 (Exit of 40)
        var movReversal = new InventoryMovement { Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Type = InventoryMovementType.Exit, Purpose = InventoryMovementPurpose.Standard, ResponsibleUser = user, OccurredAt = t2, RequestFingerprint = "fp-rev" };
        var lineReversal = new InventoryMovementLine { Id = Guid.NewGuid(), Movement = movReversal, Product = product, Unit = unit, SourceLocation = loc, Quantity = 40m, LineNumber = 1 };
        lineReversal.BalanceChanges.Add(new InventoryBalanceChange { Id = Guid.NewGuid(), MovementLine = lineReversal, Location = loc, DeltaQuantity = -40m, PreviousQuantity = 40m, ResultingQuantity = 0m });
        db.InventoryMovements.Add(movReversal);
        db.InventoryMovementLines.Add(lineReversal);

        // Mov3: Replacement Entry of 35
        var movReplacement = new InventoryMovement { Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Type = InventoryMovementType.Entry, Purpose = InventoryMovementPurpose.Standard, ResponsibleUser = user, OccurredAt = t2.AddMinutes(1), RequestFingerprint = "fp-rep" };
        var lineReplacement = new InventoryMovementLine { Id = Guid.NewGuid(), Movement = movReplacement, Product = product, Unit = unit, DestinationLocation = loc, Quantity = 35m, LineNumber = 1 };
        lineReplacement.BalanceChanges.Add(new InventoryBalanceChange { Id = Guid.NewGuid(), MovementLine = lineReplacement, Location = loc, DeltaQuantity = 35m, PreviousQuantity = 0m, ResultingQuantity = 35m });
        db.InventoryMovements.Add(movReplacement);
        db.InventoryMovementLines.Add(lineReplacement);

        // Correction link
        db.InventoryMovementCorrections.Add(new InventoryMovementCorrection
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            OriginalMovementId = movOriginal.Id,
            ReversalMovementId = movReversal.Id,
            ReplacementMovementId = movReplacement.Id,
            Reason = "Error de captura en cantidad",
            RequestedByUserId = user.Id,
            AuthorizedByUserId = user.Id,
            RequestFingerprint = "fp-corr"
        });

        db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = product.Id, LocationId = loc.Id, Quantity = 35m });
        await db.SaveChangesAsync();

        var service = new KardexReportService(db);
        var kardex = await service.GetKardexAsync(new KardexFilter(product.Id, IncludeCorrectionDetails: true));
        Assert.NotNull(kardex);

        Assert.Equal(3, kardex.Rows.Count);

        // Row 1: Original
        Assert.Equal("Original corregido", kardex.Rows[0].Status);
        Assert.Equal(40m, kardex.Rows[0].EntryQuantity);
        Assert.Equal(40m, kardex.Rows[0].RunningBalance);

        // Row 2: Reversal
        Assert.Equal("Reverso", kardex.Rows[1].Status);
        Assert.Equal(40m, kardex.Rows[1].ExitQuantity);
        Assert.Equal(0m, kardex.Rows[1].RunningBalance); // 40 - 40 = 0

        // Row 3: Replacement
        Assert.Equal("Reemplazo", kardex.Rows[2].Status);
        Assert.Equal(35m, kardex.Rows[2].EntryQuantity);
        Assert.Equal(35m, kardex.Rows[2].RunningBalance); // 0 + 35 = 35
        Assert.All(kardex.Rows, row => Assert.Equal("Error de captura en cantidad", Assert.Single(row.Corrections).Reason));

        Assert.Equal(35m, kardex.Summary.EndingBalance);
        Assert.Equal(35m, kardex.Summary.CurrentPhysicalBalance);

        // Test Export Services
        var settingsService = new WarehouseSettingsService(db);
        var exportService = new KardexExportService(settingsService);

        var excelBytes = await exportService.ToExcelAsync(kardex, DateTimeOffset.UtcNow);
        Assert.NotNull(excelBytes);
        Assert.True(excelBytes.Length > 0);

        using var stream = new MemoryStream(excelBytes);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet("Kardex");
        Assert.True(sheet.RowsUsed().Count() >= 8);

        var csvBytes = await exportService.ToCsvAsync(kardex, DateTimeOffset.UtcNow);
        var csvText = Encoding.UTF8.GetString(csvBytes);
        Assert.Contains("Original corregido", csvText);
        Assert.Contains("Reverso", csvText);
        Assert.Contains("Reemplazo", csvText);
        Assert.Contains("35.0000", csvText);
    }

    [Fact]
    public async Task Kardex_paginates_by_movement_line_and_preserves_balance_and_negative_transition()
    {
        await using var db = CreateDbContext();
        var role = new Role { Id = 1, Code = "OPERATOR", Name = "Operador" };
        var user = new User { FullName = "Operador", PinLookup = "page-lk", PinHash = "page-ph", Role = role };
        var unit = new Unit { Id = 1, Code = "PZA", Name = "Pieza" };
        var product = new Product { Sku = "SKU-PAGE", BaseUnit = unit };
        var location = new Location { Code = "PAGE-01", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        db.AddRange(role, user, unit, product, location);

        var start = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 25; index++)
        {
            var movement = new InventoryMovement { OperationId = Guid.NewGuid(), RequestFingerprint = $"page-{index}",
                Type = InventoryMovementType.Entry, Purpose = InventoryMovementPurpose.Standard,
                ResponsibleUser = user, OccurredAt = start.AddMinutes(index), RecordedAt = start.AddMinutes(index) };
            var line = new InventoryMovementLine { Movement = movement, Product = product, Unit = unit,
                DestinationLocation = location, Quantity = 1m, LineNumber = 1 };
            line.BalanceChanges.Add(new InventoryBalanceChange { MovementLine = line, Location = location,
                DeltaQuantity = 1m, PreviousQuantity = index, ResultingQuantity = index + 1 });
            db.AddRange(movement, line);
        }

        var exitMovement = new InventoryMovement { OperationId = Guid.NewGuid(), RequestFingerprint = "page-exit",
            Type = InventoryMovementType.Exit, Purpose = InventoryMovementPurpose.GeneralExit,
            ResponsibleUser = user, OccurredAt = start.AddMinutes(25), RecordedAt = start.AddMinutes(25) };
        var exitLine = new InventoryMovementLine { Movement = exitMovement, Product = product, Unit = unit,
            SourceLocation = location, Quantity = 30m, LineNumber = 1 };
        exitLine.BalanceChanges.Add(new InventoryBalanceChange { MovementLine = exitLine, Location = location,
            DeltaQuantity = -30m, PreviousQuantity = 25m, ResultingQuantity = -5m });
        db.AddRange(exitMovement, exitLine);
        db.InventoryBalances.Add(new InventoryBalance { Product = product, Location = location, Quantity = -5m });
        await db.SaveChangesAsync();

        var result = await new KardexReportService(db).GetKardexPageAsync(
            new KardexFilter(product.Id, PageNumber: 2, PageSize: 25));

        Assert.NotNull(result);
        Assert.Equal(26, result.TotalLines);
        Assert.Equal(2, result.TotalPages);
        Assert.Equal(25m, result.BalanceBeforePage);
        var row = Assert.Single(result.Rows);
        Assert.Equal(-5m, row.RunningBalance);
        Assert.True(row.IsNegative);
        Assert.True(row.PassedToNegative);
        Assert.Equal(25m, result.Summary.TotalEntries);
        Assert.Equal(30m, result.Summary.TotalExits);
        Assert.Equal(-5m, result.Summary.EndingBalance);
    }
}
