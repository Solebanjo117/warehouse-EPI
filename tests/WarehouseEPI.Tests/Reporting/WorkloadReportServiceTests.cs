using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Reporting;

public sealed class WorkloadReportServiceTests
{
    private static readonly TimeZoneInfo MexicoTz = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");

    [Fact]
    public async Task Workload_computes_exact_operations_and_lines_by_operator()
    {
        await using var db = CreateDbContext();
        var userA = CreateUser("Operador Alfa", "OPERATOR");
        var userB = CreateUser("Operador Beta", "OPERATOR");
        var product1 = CreateProduct("SKU-1");
        var product2 = CreateProduct("SKU-2");
        var locA = CreateLocation("LOC-A");
        var locB = CreateLocation("LOC-B");
        db.AddRange(userA, userB, product1, product2, locA, locB);

        // Operador A: 1 Entrada con 2 líneas, 1 Salida con 1 línea (Total = 2 ops, 3 líneas)
        AddMovement(db, userA, InventoryMovementType.Entry, new DateTimeOffset(2026, 8, 10, 15, 0, 0, TimeSpan.Zero), (product1, locA, null, 10m), (product2, locB, null, 5m));
        AddMovement(db, userA, InventoryMovementType.Exit, new DateTimeOffset(2026, 8, 10, 16, 0, 0, TimeSpan.Zero), (product1, null, locA, 2m));

        // Operador B: 1 Transferencia con 1 línea (Total = 1 op, 1 línea)
        AddMovement(db, userB, InventoryMovementType.Transfer, new DateTimeOffset(2026, 8, 10, 17, 0, 0, TimeSpan.Zero), (product2, locB, locA, 3m));

        await db.SaveChangesAsync();

        var service = CreateService(db);
        var filter = new WorkloadReportFilter(
            FromUtc: new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
            ToUtc: new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero));

        var report = await service.GetWorkloadPageAsync(filter);

        Assert.Equal(2, report.Summary.ActiveOperatorsCount);
        Assert.Equal(3, report.Summary.TotalOperations);
        Assert.Equal(4, report.Summary.TotalLines);
        Assert.Equal("Operador Alfa", report.Summary.TopOperatorName);
        Assert.Equal(2, report.Summary.TopOperatorOperations);

        Assert.Equal(2, report.Operators.Count);
        var opA = report.Operators.Single(x => x.UserId == userA.Id);
        Assert.Equal(2, opA.TotalOperations);
        Assert.Equal(3, opA.TotalLines);
        Assert.Equal(1, opA.EntryOperations);
        Assert.Equal(2, opA.EntryLines);
        Assert.Equal(1, opA.ExitOperations);
        Assert.Equal(1, opA.ExitLines);
        Assert.Equal(0, opA.TransferOperations);

        var opB = report.Operators.Single(x => x.UserId == userB.Id);
        Assert.Equal(1, opB.TotalOperations);
        Assert.Equal(1, opB.TotalLines);
        Assert.Equal(1, opB.TransferOperations);
        Assert.Equal(1, opB.TransferLines);
    }

    [Fact]
    public async Task Workload_excludes_corrected_original_and_reversal_movements()
    {
        await using var db = CreateDbContext();
        var user = CreateUser("Operador Corrector", "OPERATOR");
        var product = CreateProduct("SKU-CORR");
        var loc = CreateLocation("LOC-1");
        db.AddRange(user, product, loc);

        // Movimiento válido independiente: 1 op, 1 línea
        AddMovement(db, user, InventoryMovementType.Entry, new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero), (product, loc, null, 10m));

        // Cadena de corrección: Original (100 qty), Reverso (100 qty), Reemplazo (5 qty con 2 líneas)
        var original = AddMovement(db, user, InventoryMovementType.Exit, new DateTimeOffset(2026, 8, 10, 11, 0, 0, TimeSpan.Zero), (product, null, loc, 100m));
        var reversal = AddMovement(db, user, InventoryMovementType.Exit, new DateTimeOffset(2026, 8, 10, 11, 5, 0, TimeSpan.Zero), (product, null, loc, 100m));
        var replacement = AddMovement(db, user, InventoryMovementType.Exit, new DateTimeOffset(2026, 8, 10, 11, 10, 0, TimeSpan.Zero), (product, null, loc, 3m), (product, null, loc, 2m));

        db.InventoryMovementCorrections.Add(new InventoryMovementCorrection
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString(),
            OriginalMovementId = original.Id,
            ReversalMovementId = reversal.Id,
            ReplacementMovementId = replacement.Id,
            AuthorizedByUserId = user.Id,
            Reason = "Error corregido"
        });

        await db.SaveChangesAsync();

        var service = CreateService(db);
        var filter = new WorkloadReportFilter(
            FromUtc: new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
            ToUtc: new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero));

        var report = await service.GetWorkloadPageAsync(filter);

        Assert.Single(report.Operators);
        var op = report.Operators[0];
        // Total operaciones = 1 entrada normal + 1 reemplazo = 2 (se excluyen original y reverso)
        Assert.Equal(2, op.TotalOperations);
        // Total líneas = 1 línea entrada + 2 líneas reemplazo = 3
        Assert.Equal(3, op.TotalLines);
        Assert.Equal(1, op.EntryOperations);
        Assert.Equal(1, op.EntryLines);
        Assert.Equal(1, op.ExitOperations);
        Assert.Equal(2, op.ExitLines);
    }

    [Fact]
    public async Task Workload_filters_by_shift_respecting_boundaries_and_midnight_crossing()
    {
        await using var db = CreateDbContext();
        var user = CreateUser("Operador Turnos", "OPERATOR");
        var product = CreateProduct("SKU-SHIFT");
        var loc = CreateLocation("LOC-S");
        db.AddRange(user, product, loc);

        // En America/Mexico_City (UTC-6):
        // 05:59:59 local = 11:59:59 UTC -> NOCTURNO
        // 06:00:00 local = 12:00:00 UTC -> MATUTINO
        // 13:59:59 local = 19:59:59 UTC -> MATUTINO
        // 14:00:00 local = 20:00:00 UTC -> VESPERTINO
        // 21:59:59 local = 03:59:59 UTC (siguiente día) -> VESPERTINO
        // 22:00:00 local = 04:00:00 UTC (siguiente día) -> NOCTURNO
        // 01:30:00 local = 07:30:00 UTC (siguiente día) -> NOCTURNO (cruza medianoche)

        var date = new DateOnly(2026, 8, 15);
        DateTimeOffset ToUtc(TimeOnly time) =>
            new(date.ToDateTime(time), MexicoTz.GetUtcOffset(date.ToDateTime(time)));

        AddMovement(db, user, InventoryMovementType.Entry, ToUtc(new TimeOnly(5, 59, 59)), (product, loc, null, 1m));
        AddMovement(db, user, InventoryMovementType.Entry, ToUtc(new TimeOnly(6, 0, 0)), (product, loc, null, 1m));
        AddMovement(db, user, InventoryMovementType.Entry, ToUtc(new TimeOnly(13, 59, 59)), (product, loc, null, 1m));
        AddMovement(db, user, InventoryMovementType.Entry, ToUtc(new TimeOnly(14, 0, 0)), (product, loc, null, 1m));
        AddMovement(db, user, InventoryMovementType.Entry, ToUtc(new TimeOnly(21, 59, 59)), (product, loc, null, 1m));
        AddMovement(db, user, InventoryMovementType.Entry, ToUtc(new TimeOnly(22, 0, 0)), (product, loc, null, 1m));
        AddMovement(db, user, InventoryMovementType.Entry, ToUtc(new TimeOnly(1, 30, 0)), (product, loc, null, 1m));

        await db.SaveChangesAsync();

        var service = CreateService(db);
        var baseFilter = new WorkloadReportFilter(
            FromUtc: new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
            ToUtc: new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero));

        // 1. Matutino: 06:00:00 y 13:59:59 = 2 operaciones
        var morningReport = await service.GetWorkloadPageAsync(baseFilter with { Shift = WorkloadShift.Morning });
        Assert.Equal(2, morningReport.Summary.TotalOperations);

        // 2. Vespertino: 14:00:00 y 21:59:59 = 2 operaciones
        var afternoonReport = await service.GetWorkloadPageAsync(baseFilter with { Shift = WorkloadShift.Afternoon });
        Assert.Equal(2, afternoonReport.Summary.TotalOperations);

        // 3. Nocturno: 05:59:59, 22:00:00 y 01:30:00 = 3 operaciones
        var nightReport = await service.GetWorkloadPageAsync(baseFilter with { Shift = WorkloadShift.Night });
        Assert.Equal(3, nightReport.Summary.TotalOperations);

        // 4. Todos: 7 operaciones
        var allReport = await service.GetWorkloadPageAsync(baseFilter with { Shift = WorkloadShift.All });
        Assert.Equal(7, allReport.Summary.TotalOperations);
    }

    [Fact]
    public async Task Workload_returns_team_breakdowns_without_operator_identity_for_public_view()
    {
        await using var db = CreateDbContext();
        var user = CreateUser("Nombre no público", "OPERATOR");
        var product = CreateProduct("SKU-PUBLIC");
        var location = CreateLocation("LOC-PUBLIC");
        db.AddRange(user, product, location);
        var first = AddMovement(db, user, InventoryMovementType.Exit,
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero), (product, null, location, 2m));
        first.Purpose = InventoryMovementPurpose.ProductionIssue;
        var second = AddMovement(db, user, InventoryMovementType.Entry,
            new DateTimeOffset(2026, 8, 11, 20, 0, 0, TimeSpan.Zero), (product, location, null, 4m));
        second.Purpose = InventoryMovementPurpose.DocumentReceipt;
        await db.SaveChangesAsync();

        var report = await CreateService(db).GetWorkloadPageAsync(
            new WorkloadReportFilter(
                new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero)),
            "Prueba pública",
            includeOperatorDetails: false);

        Assert.False(report.IncludesOperatorDetails);
        Assert.Empty(report.Operators);
        Assert.Null(report.Summary.TopOperatorName);
        Assert.Equal(2, report.Summary.TotalOperations);
        Assert.Equal(2, report.DailyBreakdown.Count);
        Assert.Contains(report.PurposeBreakdown, item => item.Code == nameof(InventoryMovementPurpose.ProductionIssue) && item.Operations == 1);
        Assert.Contains(report.PurposeBreakdown, item => item.Code == nameof(InventoryMovementPurpose.DocumentReceipt) && item.Operations == 1);
        Assert.Equal(2, report.TimeBandBreakdown.Sum(item => item.Operations));
    }

    [Fact]
    public async Task Workload_exports_excel_and_csv_correctly()
    {
        await using var db = CreateDbContext();
        var user = CreateUser("Operador Export", "OPERATOR");
        var product = CreateProduct("SKU-EXP");
        var loc = CreateLocation("LOC-EXP");
        db.AddRange(user, product, loc);

        AddMovement(db, user, InventoryMovementType.Entry, new DateTimeOffset(2026, 8, 10, 15, 0, 0, TimeSpan.Zero), (product, loc, null, 25m));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var filter = new WorkloadReportFilter(
            FromUtc: new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
            ToUtc: new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero));

        var (operators, summary, _, _, _) = await service.GetWorkloadExportAsync(filter);
        var exportService = new ReportExportService(new WarehouseSettingsService(db));

        // Excel
        var excelBytes = await exportService.ExportWorkloadToExcelAsync(operators, summary, filter, "Período Test");
        Assert.NotEmpty(excelBytes);

        using var stream = new MemoryStream(excelBytes);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet("Carga de Trabajo");
        Assert.NotNull(sheet);
        Assert.Equal("Operador Export", sheet.Cell(6, 1).GetString());
        Assert.Equal(1, sheet.Cell(6, 3).GetValue<int>());
        Assert.Equal("SUM(C6:C6)", sheet.Cell(7, 3).FormulaA1);

        // CSV
        var csvBytes = await exportService.ExportWorkloadToCsvAsync(operators, summary, filter, "Período Test");
        var csvText = Encoding.UTF8.GetString(csvBytes);
        Assert.Contains("Operador Export", csvText);
        Assert.Contains("OPERATOR", csvText);
    }

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new WarehouseDbContext(options);
        db.BusinessSettings.Add(new BusinessSettings
        {
            BusinessName = "EPI Global",
            WarehouseName = "Almacén Central",
            WarehouseCode = "WH-01",
            TimeZoneId = "America/Mexico_City"
        });
        db.Roles.AddRange(
            new Role { Id = 1, Code = "OPERATOR", Name = "OPERATOR" },
            new Role { Id = 2, Code = "ADMIN", Name = "ADMIN" }
        );
        db.Units.Add(new Unit { Id = 1, Code = "PZA", Name = "Pieza" });
        db.SaveChanges();
        return db;
    }

    private static WorkloadReportService CreateService(WarehouseDbContext db) =>
        new(db, new WarehouseSettingsService(db));

    private static User CreateUser(string fullName, string roleName) => new()
    {
        Id = Guid.NewGuid(),
        FullName = fullName,
        PinLookup = Guid.NewGuid().ToString(),
        PinHash = "dummy-hash",
        RoleId = roleName == "ADMIN" ? (short)2 : (short)1
    };

    private static Product CreateProduct(string sku) => new()
    {
        Id = Guid.NewGuid(),
        Sku = sku,
        Description = $"Producto {sku}",
        BaseUnitId = 1,
        IsActive = true
    };

    private static Location CreateLocation(string code) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        Kind = LocationKind.Rack,
        OperationalRole = LocationOperationalRole.Storage,
        IsActive = true,
        IsBlocked = false
    };

    private static InventoryMovement AddMovement(
        WarehouseDbContext db,
        User user,
        InventoryMovementType type,
        DateTimeOffset occurredAt,
        params (Product Product, Location? Dest, Location? Src, decimal Qty)[] lines)
    {
        var movement = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString(),
            Type = type,
            Purpose = InventoryMovementPurpose.Standard,
            ResponsibleUserId = user.Id,
            ResponsibleUser = user,
            OccurredAt = occurredAt,
            RecordedAt = occurredAt
        };

        foreach (var (product, dest, src, qty) in lines)
        {
            movement.Lines.Add(new InventoryMovementLine
            {
                Id = Guid.NewGuid(),
                MovementId = movement.Id,
                ProductId = product.Id,
                Product = product,
                UnitId = 1,
                Quantity = qty,
                SourceLocationId = src?.Id,
                SourceLocation = src,
                DestinationLocationId = dest?.Id,
                DestinationLocation = dest
            });
        }

        db.InventoryMovements.Add(movement);
        return movement;
    }
}
