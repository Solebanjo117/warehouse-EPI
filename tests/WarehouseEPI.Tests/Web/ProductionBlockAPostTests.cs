using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionBlockAPostTests
{
    [Fact]
    public async Task Real_form_preserves_bad_decimal_clears_pin_and_activates_explicit_rework()
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var pins = scope.ServiceProvider.GetRequiredService<UserPinService>();
        var user = new User { FullName = "Operación bloque A", RoleId = 1, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(user, "6724");
        var product = new Product { Sku = "BLOCK-A", BaseUnitId = 1 };
        var material = new Product { Sku = "BLOCK-A-MP", BaseUnitId = 1 };
        var stage = new ProductionStage { Code = "BLOCK-A", Name = "Proceso A" };
        var wip = new Location { Code = "BLOCK-A-WIP", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        stage.WipTargets.Add(new ProductionProcessWipTarget { Location = wip });
        var route = new ProductionRoute { Product = product, Name = "Ruta A" };
        route.Stages.Add(new ProductionRouteStage { Stage = stage, Sequence = 1 });
        var shift = new ProductionShift { Code = "BLOCK-A", Name = "Turno A" };
        db.AddRange(user, material, route, shift);
        await db.SaveChangesAsync();
        var trace = scope.ServiceProvider.GetRequiredService<ProductionTraceabilityService>();
        var production = scope.ServiceProvider.GetRequiredService<ProductionService>();
        Assert.True((await trace.SaveRecipeAsync(new(product.Id, 1, [new(material.Id, stage.Id, 1)], "Alta", "6724"))).Success);
        var created = await production.CreateOrderAsync(new(Guid.NewGuid(), product.Id, 5, null, null, null, "6724"));
        var order = await db.ProductionWorkOrders.Include(x => x.Stages).SingleAsync(x => x.Id == created.WorkOrderId);
        Assert.Equal(ProductionCommandStatus.Success, (await production.ReleaseAsync(new(Guid.NewGuid(), order.Id, order.Version, "6724"))).Status);
        var batch = await trace.CreateBatchAsync(new(Guid.NewGuid(), order.Id, 5, order.Version, "6724"));
        Assert.True(batch.Success);
        var page = $"/Operations/Production/Work?id={order.Id}&BatchId={batch.Id}";
        var initial = await client.GetAsync(page);
        var html = await initial.Content.ReadAsStringAsync();
        Assert.True(initial.IsSuccessStatusCode, $"GET {initial.StatusCode}: {html}");
        string Token(string body) => WebUtility.HtmlDecode(Regex.Match(body, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html),
            ["BatchResult.OperationId"] = Guid.NewGuid().ToString(),
            ["BatchResult.BatchId"] = batch.Id!.Value.ToString(),
            ["BatchResult.StageId"] = order.Stages.Single().Id.ToString(),
            ["BatchResult.ShiftId"] = shift.Id.ToString(),
            ["BatchResult.ExpectedVersion"] = order.Version.ToString(),
            ["BatchResult.InputQuantity"] = "0,25",
            ["BatchResult.GoodQuantity"] = "0",
            ["BatchResult.ReworkQuantity"] = "5",
            ["BatchResult.ScrapQuantity"] = "0",
            ["BatchResult.Pin"] = "6724",
            ["BatchResult.IsRework"] = "false",
            ["BatchResult.DifferenceReason"] = "Revisión de material"
        };
        var failed = await client.PostAsync(page + "&handler=BatchResult", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        var failedHtml = await failed.Content.ReadAsStringAsync();
        Assert.Contains("0,25", failedHtml);
        Assert.Contains("punto decimal", failedHtml);
        Assert.DoesNotContain("value=\"6724\"", failedHtml);
        Assert.Empty(await db.ProductionBatchResults.ToListAsync());
        fields["__RequestVerificationToken"] = Token(failedHtml); fields["BatchResult.InputQuantity"] = "5";
        var first = await client.PostAsync(page + "&handler=BatchResult", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        db.ChangeTracker.Clear();
        var original = Assert.Single(await db.ProductionBatchResults.ToListAsync());
        var rework = Assert.Single(await db.ProductionReworkCases.ToListAsync());
        html = await client.GetStringAsync(page + "&ReworkCaseId=" + rework.Id);
        Assert.Contains("Atender retrabajo", html);
        fields["__RequestVerificationToken"] = Token(html); fields["BatchResult.OperationId"] = Guid.NewGuid().ToString();
        fields["BatchResult.ExpectedVersion"] = (await db.ProductionWorkOrders.SingleAsync(x => x.Id == order.Id)).Version.ToString();
        fields["BatchResult.IsRework"] = "true"; fields["BatchResult.ReworkCaseId"] = rework.Id.ToString();
        fields["BatchResult.InputQuantity"] = "3"; fields["BatchResult.GoodQuantity"] = "3"; fields["BatchResult.ReworkQuantity"] = "0";
        var recovered = await client.PostAsync(page + "&handler=BatchResult", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(2, await db.ProductionBatchResults.CountAsync());
        Assert.Single(await db.ProductionReworkAttempts.ToListAsync());
        Assert.Equal(5, original.InputQuantity);
        Assert.Empty(await db.ProductionMaterialOperations.ToListAsync());
        db.ChangeTracker.Clear();
        html = await client.GetStringAsync(page);
        var forbiddenConsumption = new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(html), ["Material.OperationId"] = Guid.NewGuid().ToString(), ["Material.Mode"] = "consume", ["Material.Pin"] = "6724", ["Material.StageId"] = order.Stages.Single().Id.ToString() };
        var rejected = await client.PostAsync(page + "&handler=Material", new FormUrlEncodedContent(forbiddenConsumption));
        Assert.Contains("Registra el consumo junto", await rejected.Content.ReadAsStringAsync());
        Assert.Empty(await db.ProductionMaterialOperations.ToListAsync());
        var destination = new Location { Code = "BLOCK-A-DEST", Kind = LocationKind.Area };
        db.Locations.Add(destination); db.ProductLocationAssignments.Add(new() { ProductId = material.Id, Location = destination }); await db.SaveChangesAsync();
        html = await client.GetStringAsync(page);
        var receipt = new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(html), ["Warehouse.OperationId"] = Guid.NewGuid().ToString(), ["Warehouse.BatchId"] = batch.Id.Value.ToString(), ["Warehouse.FinalStageId"] = order.Stages.Single().Id.ToString(), ["Warehouse.Quantity"] = "1", ["Warehouse.DestinationLocationId"] = destination.Id.ToString(), ["Warehouse.Pin"] = "6724", ["Warehouse.ExpectedVersion"] = (await db.ProductionWorkOrders.SingleAsync(x => x.Id == order.Id)).Version.ToString() };
        var review = await client.PostAsync(page + "&handler=Warehouse", new FormUrlEncodedContent(receipt));
        var reviewHtml = await review.Content.ReadAsStringAsync();
        Assert.Contains("Revisar ubicación compartida", WebUtility.HtmlDecode(reviewHtml));
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        receipt["__RequestVerificationToken"] = Token(reviewHtml); receipt["Warehouse.Approvals[0].Selected"] = "true"; receipt["Warehouse.Approvals[0].ProductId"] = product.Id.ToString(); receipt["Warehouse.Approvals[0].LocationId"] = destination.Id.ToString();
        await client.PostAsync(page + "&handler=Warehouse", new FormUrlEncodedContent(receipt));
        Assert.Single(await db.InventoryMovements.ToListAsync());
        var movement = await db.InventoryMovements.SingleAsync();
        var proof = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/Receipt/{movement.Id}"));
        Assert.Contains("Recepción de producción", proof); Assert.Contains("Volver a la orden", proof); Assert.Contains("Recibir otra parcialidad", proof); Assert.Contains(order.Id.ToString(), proof); Assert.DoesNotContain("Nueva operación", proof);
        await client.PostAsync(page + "&handler=Warehouse", new FormUrlEncodedContent(receipt));
        Assert.Single(await db.InventoryMovements.ToListAsync());
        Assert.Single(await db.ProductionEvents.Where(x => x.WorkOrderId == order.Id && x.Type == ProductionEventType.WarehouseReceived).ToListAsync());
        db.ChangeTracker.Clear(); var latest = await db.ProductionWorkOrders.SingleAsync(x => x.Id == order.Id);
        Assert.Equal(ProductionCommandStatus.Success, (await production.PauseAsync(new(Guid.NewGuid(), order.Id, latest.Version, "6724", "Revisión B"))).Status);
        html = await client.GetStringAsync(page); Assert.Contains("Reanudar orden", WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("data-handler=\"BatchResult\"", html);
        fields["__RequestVerificationToken"] = Token(html); fields["BatchResult.OperationId"] = Guid.NewGuid().ToString(); fields["BatchResult.ExpectedVersion"] = latest.Version.ToString();
        var manipulated = await client.PostAsync(page + "&handler=BatchResult", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, manipulated.StatusCode); Assert.Equal(2, await db.ProductionBatchResults.CountAsync());

    }
}
