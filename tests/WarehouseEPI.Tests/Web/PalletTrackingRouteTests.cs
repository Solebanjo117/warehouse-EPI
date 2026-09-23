using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Web;

public sealed class PalletTrackingRouteTests
{
    [Fact]
    public async Task Operational_forms_hide_manual_pallet_capture_and_apply_automatic_tracking()
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var pins = scope.ServiceProvider.GetRequiredService<UserPinService>();
        var user = new User { FullName = "Placas web", RoleId = 2, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(user, "7894");
        var product = new Product { Sku = "WEB-PLATES", BaseUnitId = 1 };
        var source = new Location { Code = "WEB-PLATES-A", Kind = LocationKind.Area };
        var destination = new Location { Code = "WEB-PLATES-B", Kind = LocationKind.Area };
        db.AddRange(user, product, source, destination); await db.SaveChangesAsync();
        var html = await client.GetStringAsync("/Operations/Entry");
        Assert.DoesNotContain("Pallets y license plates", html);
        Assert.DoesNotContain("Input.Pallets", html);
        var payload = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html), ["Input.OperationId"] = Guid.NewGuid().ToString(), ["Input.ProductId"] = product.Id.ToString(),
            ["Input.DestinationLocationId"] = source.Id.ToString(), ["Input.Quantity"] = "100", ["Input.Pin"] = "7894"
        };
        var response = await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(payload));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.PalletPlates.Where(x => x.ProductId == product.Id).ToListAsync());
        var entryMovement = await db.InventoryMovements.SingleAsync(x => x.Lines.Any(line => line.ProductId == product.Id));
        var globalHistoryHtml = WebUtility.HtmlDecode(await client.GetStringAsync("/Operations/PalletLabels"));
        Assert.Contains("Últimos 10 movimientos", globalHistoryHtml);
        Assert.Contains(product.Sku, globalHistoryHtml);
        Assert.Contains("Imprimir placa", globalHistoryHtml);
        Assert.Contains($"name=\"MovementPrint.MovementId\" value=\"{entryMovement.Id}\"", globalHistoryHtml);
        Assert.Contains("PrepareMovementPrint", globalHistoryHtml);
        response = await client.GetAsync($"/Operations/PalletLabels?location={source.Code}&productId={product.Id}");
        html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.True(response.IsSuccessStatusCode, html);
        Assert.Contains(product.Sku, html); Assert.Contains("Stock total", html); Assert.Contains("100", html);
        Assert.DoesNotContain("En placas", html); Assert.DoesNotContain("Sin placa", html); Assert.DoesNotContain("Libre para identificar", html);
        Assert.Contains("Últimos 10 movimientos", html); Assert.DoesNotContain("Ver comprobante", html);
        Assert.Contains("Imprimir placa", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Identify.Pin", html); Assert.DoesNotContain("NIP del responsable", html);
        Assert.DoesNotContain("alert alert-danger", html, StringComparison.Ordinal);
        var productOptions = await client.GetStringAsync($"/Operations/PalletLabels?handler=ProductOptions&q=WEB&locationId={source.Id}");
        Assert.Contains(product.Sku, productOptions); Assert.DoesNotContain(destination.Code, productOptions);
        var locationOptions = await client.GetStringAsync($"/Operations/PalletLabels?handler=LocationOptions&q=WEB&productId={product.Id}");
        Assert.Contains(source.Code, locationOptions); Assert.DoesNotContain(destination.Code, locationOptions);
        var identify = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html), ["Identify.OperationId"] = Value(html, "Identify.OperationId"),
            ["Identify.LocationId"] = Value(html, "Identify.LocationId"), ["Identify.ProductId"] = Value(html, "Identify.ProductId"),
            ["Identify.ExpectedBalanceVersion"] = Value(html, "Identify.ExpectedBalanceVersion"),
            ["Identify.ExpectedIdentifiable"] = Value(html, "Identify.ExpectedIdentifiable"), ["Identify.Quantity"] = Value(html, "Identify.Quantity")
        };
        response = await client.PostAsync("/Operations/PalletLabels?handler=Identify", new FormUrlEncodedContent(identify));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("#label-preview", response.RequestMessage?.RequestUri?.Fragment);
        db.ChangeTracker.Clear();
        var plate = await db.PalletPlates.SingleAsync(x => x.ProductId == product.Id);
        Assert.Null((await db.PalletPlateEvents.SingleAsync(x => x.PlateId == plate.Id && x.Kind == "Identification")).ResponsibleUserId);
        html = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/PalletLabels?location={source.Code}&productId={product.Id}"));
        Assert.Contains("Imprimir placa", html);
        Assert.DoesNotContain("Crear placa", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Reimprimir placa", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"id={plate.Id}", html);
        html = await client.GetStringAsync("/Operations/Transfer");
        Assert.DoesNotContain("Pallets y license plates", html);
        Assert.DoesNotContain("Input.Pallets", html);
        var transfer = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html), ["Input.OperationId"] = Guid.NewGuid().ToString(), ["Input.ProductId"] = product.Id.ToString(),
            ["Input.SourceLocationId"] = source.Id.ToString(), ["Input.DestinationLocationId"] = destination.Id.ToString(), ["Input.Quantity"] = "20", ["Input.Pin"] = "7894"
        };
        response = await client.PostAsync("/Operations/Transfer", new FormUrlEncodedContent(transfer));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); db.ChangeTracker.Clear();
        Assert.Equal(2, await db.PalletPlates.CountAsync(x => x.ProductId == product.Id));
        Assert.Equal(80, (await db.PalletPlates.SingleAsync(x => x.Id == plate.Id)).Quantity);
        var balance = await scope.ServiceProvider.GetRequiredService<InventoryQueryService>().GetBalanceAsync(product.Id, source.Id);
        var adjustment = await scope.ServiceProvider.GetRequiredService<InventoryMovementService>().ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Adjustment, "7894",
            [new(product.Id, 85, LocationId: source.Id, ExpectedBalanceVersion: balance.Version, AutomaticPalletHandling: true)],
            Notes: "Conteo de placa"));
        Assert.Equal(InventoryMovementStatus.Success, adjustment.Status);
        db.ChangeTracker.Clear();
        Assert.Equal(85, (await db.PalletPlates.SingleAsync(x => x.Id == plate.Id)).Quantity);
        html = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/PalletLabels?id={adjustment.MovementId}&print=true"));
        Assert.DoesNotContain("La placa requiere una Entrada", html);
        Assert.Contains(">85 ", html);
        html = WebUtility.HtmlDecode(await client.GetStringAsync("/Operations/PalletLabels"));
        response = await client.PostAsync("/Operations/PalletLabels?handler=PrepareMovementPrint", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html),
            ["MovementPrint.OperationId"] = Guid.NewGuid().ToString(),
            ["MovementPrint.MovementId"] = adjustment.MovementId!.Value.ToString()
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"id={plate.Id}", response.RequestMessage?.RequestUri?.Query);
        html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("field Quantity", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ese movimiento no tiene una placa", html, StringComparison.OrdinalIgnoreCase);
        response = await client.PostAsync("/Operations/PalletLabels?handler=Print", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html),
            ["Input.MovementId"] = plate.Id.ToString(),
            ["Input.Copies"] = "2"
        }));
        html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"value=\"{plate.Id}\"", html);
        Assert.DoesNotContain("field Quantity", html, StringComparison.OrdinalIgnoreCase);
        html = WebUtility.HtmlDecode(await client.GetStringAsync("/Operations/PalletLabels"));
        Assert.Contains("Imprimir placa", html);
        Assert.Contains($"id={plate.Id}", html);
        Assert.Contains("print=true", html);
        Assert.DoesNotContain("Ver comprobante", html);
        html = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/PalletLabels/Tracking?id={plate.Id}"));
        Assert.Contains("100 → 80", html); Assert.Contains("Transferencia", html); Assert.Contains("Placas web", html);
        var blind = await client.GetStringAsync($"/Operations/PalletLabels/Tracking?handler=Available&productId={product.Id}&locationId={source.Id}&blind=true");
        Assert.DoesNotContain("\"quantity\":80", blind); Assert.Contains("Por contar", blind);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Operations/PalletLabels/Tracking?handler=Activate", new FormUrlEncodedContent([]))).StatusCode);
    }

    private static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    private static string Value(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
}
