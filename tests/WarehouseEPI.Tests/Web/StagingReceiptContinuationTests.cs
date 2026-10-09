using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Labels;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Web;

public sealed class StagingReceiptContinuationTests
{
    [Theory]
    [InlineData(InventoryMovementPurpose.Standard, "es")]
    [InlineData(InventoryMovementPurpose.DocumentReceipt, "es")]
    [InlineData(InventoryMovementPurpose.ProductionReceipt, "es")]
    [InlineData(InventoryMovementPurpose.Standard, "en")]
    public async Task Receipt_routes_each_line_and_previews_only_current_arrival(InventoryMovementPurpose purpose, string language)
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{WarehouseEPI.Web.Localization.UiLanguage.CookieName}={language}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var user = new User { FullName = "Receipt test", RoleId = 2, PinLookup = "", PinHash = "" };
        await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "7854");
        var product = new Product { Sku = "REPEATED-SKU", BaseUnitId = 1 };
        var staging = new Location { Code = "STAGING", Kind = LocationKind.Area };
        var other = new Location { Code = "OTHER", Kind = LocationKind.Area };
        var preset = PalletLicensePlatePresetCatalog.Initial;
        var template = new LabelTemplate { Code = preset.Code, Kind = LabelTemplateKind.PalletLicensePlate };
        var version = new LabelTemplateVersion { Template = template, Name = preset.Name, Version = 1,
            SizePreset = preset.Size, Status = LabelTemplateStatus.Published, DesignJson = LabelDesignSerializer.Serialize(preset.Design) };
        template.CurrentPublishedVersion = version; template.CurrentPublishedVersionId = version.Id;
        db.AddRange(user, product, staging, other, template, version); await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<InventoryMovementService>();
        var result = await service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, "7854",
            [new(product.Id, 40, DestinationLocationId: staging.Id), new(product.Id, 60, DestinationLocationId: staging.Id),
             new(product.Id, 20, DestinationLocationId: other.Id)], Purpose: purpose));
        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        var query = new StagingArrivalQuery(db);
        var rows = await query.ForMovementAsync(result.MovementId!.Value);
        Assert.Equal(2, rows.Count);
        var receiptRaw = await client.GetStringAsync($"/Operations/Receipt/{result.MovementId}");
        var receipt = WebUtility.HtmlDecode(receiptRaw);
        Assert.Equal(2, Regex.Count(receipt, "data-staging-arrival="));
        Assert.Contains("/Operations/Entry?mode=staging", receipt);
        foreach (var row in rows) Assert.Contains($"arrivalLineId={row.LineId}", receipt);
        var first = rows.Single(r => r.Received == 40);
        var secondPlate = StagingArrivalQuery.DecodeVersion(rows.Single(r => r.Received == 60).Version)!.Single();
        var before = (db.InventoryMovements.Count(), db.PalletPlates.Count(), db.PalletPlateEvents.Count());
        var focusedUrl = $"/Operations/Staging?arrivalLineId={first.LineId}&pageNumber=999&search=missing";
        var focusedRaw = await client.GetStringAsync(focusedUrl);
        var focused = WebUtility.HtmlDecode(focusedRaw);
        Assert.Contains($"arrival={first.LineId}", focused);
        Assert.DoesNotContain($"arrival={rows.Single(r => r.Received == 60).LineId}", focused);
        var printUrl = $"/Operations/PalletLabels?handler=StagingArrival&arrivalLineId={first.LineId}";
        var printedRaw = await client.GetStringAsync(printUrl);
        var printed = WebUtility.HtmlDecode(printedRaw);
        Assert.Equal(1, Regex.Count(printed, "class=\"label-dynamic\""));
        Assert.DoesNotContain($"PLT-{secondPlate.PlateId:N}".ToUpperInvariant(), printed);
        Assert.Equal(before, (db.InventoryMovements.Count(), db.PalletPlates.Count(), db.PalletPlateEvents.Count()));
        var directory = Environment.GetEnvironmentVariable("WAREHOUSE_STAGING_RECEIPT_FIXTURES");
        if (!string.IsNullOrEmpty(directory) && purpose == InventoryMovementPurpose.Standard)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, $"receipt-{language}.html"), receiptRaw);
            await File.WriteAllTextAsync(Path.Combine(directory, $"arrival-{language}.html"), focusedRaw);
            await File.WriteAllTextAsync(Path.Combine(directory, $"plates-{language}.html"), printedRaw);
        }
        var selected = StagingArrivalQuery.DecodeVersion(first.Version)!.Single();
        Assert.Equal(InventoryMovementStatus.Success, (await service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Transfer, "7854",
            [new(product.Id, 10, SourceLocationId: staging.Id, DestinationLocationId: other.Id,
                Plates: [selected with { Quantity = 10 }])]))).Status);
        first = (await query.GetAsync(first.LineId))!;
        Assert.Equal(30, first.Pending);
        Assert.Contains("class=\"label-dynamic\"", await client.GetStringAsync(printUrl));
        var putaway = await service.ConfirmStagingAsync(new(Guid.NewGuid(), first.LineId, other.Id, first.Version, "7854"));
        Assert.Equal(InventoryMovementStatus.Success, putaway.Status);
        printed = WebUtility.HtmlDecode(await client.GetStringAsync(printUrl));
        Assert.DoesNotContain("class=\"label-dynamic\"", printed);
        Assert.Contains(language == "es" ? "Esta llegada ya no tiene material pendiente en STAGING" :
            "This arrival no longer has material pending in STAGING", printed);
        Assert.DoesNotContain($"arrival={first.LineId}", await client.GetStringAsync(focusedUrl));
        var outsideLine = await db.InventoryMovementLines.SingleAsync(l => l.MovementId == result.MovementId && l.DestinationLocationId == other.Id);
        foreach (var id in new[] { Guid.NewGuid(), outsideLine.Id })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Operations/Staging?arrivalLineId={id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Operations/PalletLabels?handler=StagingArrival&arrivalLineId={id}")).StatusCode);
        }
        var historical = new InventoryMovement { OperationId = Guid.NewGuid(), RequestFingerprint = "history", Type = InventoryMovementType.Entry,
            ResponsibleUserId = user.Id, Lines = [new InventoryMovementLine { ProductId = product.Id, UnitId = 1, Quantity = 5,
                DestinationLocationId = staging.Id, LineNumber = 1 }] };
        db.InventoryMovements.Add(historical); await db.SaveChangesAsync();
        var historicalLine = historical.Lines.Single().Id;
        var historicalUrl = $"/Operations/Staging?arrivalLineId={historicalLine}";
        var historicalHtml = await client.GetStringAsync(historicalUrl);
        var historicalPrint = await client.GetStringAsync($"/Operations/PalletLabels?handler=StagingArrival&arrivalLineId={historicalLine}");
        Assert.DoesNotContain("class=\"label-dynamic\"", historicalPrint);
        Assert.Contains($"arrivalLineId={historicalLine}", historicalPrint);
        var failed = await client.PostAsync("/Operations/Staging?handler=Identify", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["ArrivalLineId"] = historicalLine.ToString(), ["Arrival"] = historicalLine.ToString(),
            ["OperationId"] = Guid.NewGuid().ToString(), ["PhysicalQuantity"] = "0",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(historicalHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value)
        }));
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Contains($"value=\"{historicalLine}\"", await failed.Content.ReadAsStringAsync());
        var historicalReceipt = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/Receipt/{historical.Id}"));
        Assert.DoesNotContain(language == "es" ? ">Identificar pallet<" : ">Identify pallet<", historicalReceipt);
        db.InventoryMovementCorrections.Add(new() { OriginalMovementId = result.MovementId.Value, ReversalMovementId = Guid.NewGuid(), Reason = "Test", RequestFingerprint = "correction" });
        await db.SaveChangesAsync();
        Assert.DoesNotContain("data-staging-arrival", await client.GetStringAsync($"/Operations/Receipt/{result.MovementId}"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(printUrl)).StatusCode);
    }
}
