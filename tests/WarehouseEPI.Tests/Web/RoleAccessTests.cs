using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using static WarehouseEPI.Tests.Web.ProductionCaptureRecoveryTests;

namespace WarehouseEPI.Tests.Web;

public sealed class RoleAccessTests
{
    [Theory]
    [InlineData("ADMIN", "0123")]
    [InlineData("OPERATOR", "4567")]
    [InlineData("PRODUCTION", "6789")]
    public async Task Sessions_enforce_direct_routes_and_are_revoked_when_role_changes(string role, string pin)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        await SeedAsync(factory.Services);
        await AddUsers(factory.Services);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        foreach (var path in new[] { "/", "/Modules/operations", "/Inventory", "/Locations", "/Operations/Production", "/Operations/Labels", "/Operations/PalletLabels", "/Reports/Notifications", "/Operations/Lookup?handler=Products&q=RECOVER" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        await Fixture("public-home", await client.GetStringAsync("/"));
        await Fixture("notifications", await client.GetStringAsync("/Reports/Notifications"));
        await Fixture("balance", await client.GetStringAsync("/Operations/Production"));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Reports/Inventory")).StatusCode);
        var anonymousInventory = await client.GetStringAsync("/Modules/inventory");
        Assert.DoesNotContain("href=\"/Reports/Notifications", anonymousInventory);
        Assert.DoesNotContain("data-notification-trigger", anonymousInventory);
        Assert.DoesNotContain("id=\"operational-notifications\"", anonymousInventory);
        var login = await client.GetStringAsync("/Admin/Login");
        var signedIn = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Input.Pin"] = pin, ["__RequestVerificationToken"] = Input(login, "__RequestVerificationToken") }));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        var signedInInventory = await client.GetStringAsync("/Modules/inventory");
        Assert.Contains("href=\"/Reports/Notifications", signedInInventory);
        Assert.Contains("notification-trigger-mobile", signedInInventory);
        Assert.Contains("notification-trigger-desktop", signedInInventory);
        Assert.Contains("id=\"operational-notifications\"", signedInInventory);
        foreach (var (path, allowed) in new[]
        {
            ("/Admin/Catalogs/Products", role != "OPERATOR"), ("/Admin/Catalogs/Products/Create", role == "ADMIN"),
            ("/Admin/Catalogs/Products/Import", role == "ADMIN"), ("/Admin/Inventory/Movements", role != "PRODUCTION"),
            ("/Production/Schedule", role != "OPERATOR"), ("/Admin/Production/Schedule", role == "ADMIN"),
            ("/Reports/Inventory", role == "ADMIN"), ("/Admin/Users", role == "ADMIN"),
            ("/Operations/Production/Advanced", role == "ADMIN")
        })
        {
            var response = await client.GetAsync(path);
            if (allowed) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            else
            {
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                Assert.Equal("/AccessDenied", response.Headers.Location?.AbsolutePath);
            }
        }
        if (role == "PRODUCTION")
        {
            var products = await client.GetStringAsync("/Admin/Catalogs/Products");
            Assert.DoesNotContain("href=\"/Admin/Catalogs/Products/Create", products);
            Assert.DoesNotContain("href=\"/Admin/Catalogs/Products/Edit", products);
            var schedule = await client.GetStringAsync("/Production/Schedule");
            Assert.DoesNotContain("method=\"post\"", schedule.Split("<main", StringSplitOptions.None)[1]);
            await Fixture("schedule", schedule);
            await Fixture("products", products);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var actor = await scope.ServiceProvider.GetRequiredService<UserPinService>().AuthenticateAsync(pin);
            var user = await db.Users.SingleAsync(x => x.Id == actor!.Id);
            user.RoleId = role == "ADMIN" ? (short)2 : (short)1;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Admin/Users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Operations/Entry")).StatusCode);
        var revokedInventory = await client.GetStringAsync("/Modules/inventory");
        Assert.DoesNotContain("href=\"/Reports/Notifications", revokedInventory);
        Assert.DoesNotContain("data-notification-trigger", revokedInventory);
    }

    [Fact]
    public async Task Production_pin_rules_cover_all_confirmations_and_retries_without_writes_on_rejection()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        await AddUsers(factory.Services);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ProductionDailyCaptureService>();
        var single = new ConfirmProductionDailyCaptureCommand(Guid.NewGuid(), seed.Date, ProductionDailyArea.Cutting, seed.ShiftId, seed.Products[0], 2, null, "4567");
        var group = new ProductionCaptureGroupCommand(Guid.NewGuid(), seed.Date, ProductionDailyArea.Cutting, seed.ShiftId, [new(seed.Products[1], 3, null)], Pin: "4567");
        var matrix = new ProductionMatrixCommand(Guid.NewGuid(), seed.Date, seed.ShiftId, [new(seed.Products[2], ProductionDailyArea.Cutting, 4, null)], Pin: "4567");
        var edit = new ProductionBalanceEditCommand(Guid.NewGuid(), seed.WeekId, seed.Date, [new(seed.Products[0], ProductionDailyArea.Cutting, 1, 0, 1)], Pin: "4567");
        foreach (var result in new[] { await service.ConfirmAsync(single), await service.ConfirmGroupAsync(group), await service.ConfirmMatrixAsync(matrix), await service.ConfirmBalanceEditAsync(edit) })
        {
            Assert.Equal(ProductionDailyCommandStatus.RoleNotAllowed, result.Status);
            Assert.Contains(RoleAccess.ProductionWarning, result.Errors!);
        }
        Assert.Empty(await db.ProductionDailyCaptures.ToListAsync());
        single = single with { Pin = "6789" };
        Assert.True((await service.ConfirmAsync(single)).Success);
        group = group with { Pin = "6789", ReviewedFingerprint = (await service.PreviewGroupAsync(group)).Fingerprint };
        Assert.True((await service.ConfirmGroupAsync(group)).Success);
        matrix = matrix with { Pin = "6789", ReviewedFingerprint = (await service.PreviewMatrixAsync(matrix)).Fingerprint };
        Assert.True((await service.ConfirmMatrixAsync(matrix)).Success);
        edit = edit with { Pin = "6789", Cells = [new(seed.Products[0], ProductionDailyArea.Cutting, 1, 2, 3)] };
        edit = edit with { ReviewedFingerprint = (await service.PreviewBalanceEditAsync(edit)).Fingerprint };
        Assert.True((await service.ConfirmBalanceEditAsync(edit)).Success);
        Assert.True((await service.ConfirmAsync(single)).Success);
        Assert.True((await service.ConfirmGroupAsync(group)).Success);
        Assert.True((await service.ConfirmMatrixAsync(matrix)).Success);
        Assert.True((await service.ConfirmBalanceEditAsync(edit)).Success);
        var production = await db.Users.SingleAsync(x => x.FullName == "Role production");
        Assert.All(await db.ProductionDailyCaptures.ToListAsync(), x => Assert.Equal(production.Id, x.ResponsibleUserId));
        var count = await db.ProductionDailyCaptures.CountAsync();
        production.RoleId = 2; await db.SaveChangesAsync();
        foreach (var result in new[] { await service.ConfirmAsync(single), await service.ConfirmGroupAsync(group), await service.ConfirmMatrixAsync(matrix), await service.ConfirmBalanceEditAsync(edit) })
            Assert.Equal(ProductionDailyCommandStatus.RoleNotAllowed, result.Status);
        production.IsActive = false; await db.SaveChangesAsync();
        Assert.Equal(ProductionDailyCommandStatus.InvalidPin, (await service.ConfirmAsync(single)).Status);
        Assert.Equal(ProductionDailyCommandStatus.InvalidPin, (await service.ConfirmMatrixAsync(matrix with { Pin = "9999" })).Status);
        Assert.Equal(count, await db.ProductionDailyCaptures.CountAsync());
    }

    [Fact]
    public async Task Reductions_and_reversals_require_admin_pin_and_reason()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        await AddUsers(factory.Services);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ProductionDailyCaptureService>();
        var captured = await service.ConfirmAsync(new(Guid.NewGuid(), seed.Date, ProductionDailyArea.Cutting, seed.ShiftId, seed.Products[0], 5, null, "6789"));
        Assert.True(captured.Success);
        var edit = new ProductionBalanceEditCommand(Guid.NewGuid(), seed.WeekId, seed.Date, [new(seed.Products[0], ProductionDailyArea.Cutting, 1, 5, 2)], Pin: "6789");
        var preview = await service.PreviewBalanceEditAsync(edit);
        Assert.True(preview.RequiresAdmin);
        Assert.True(preview.RequiresReason);
        edit = edit with { ReviewedFingerprint = preview.Fingerprint };
        Assert.False((await service.ConfirmBalanceEditAsync(edit)).Success);
        Assert.False((await service.ConfirmBalanceEditAsync(edit with { Pin = "0123" })).Success);
        edit = edit with { Pin = "0123", Reason = "Correct shift total" };
        edit = edit with { ReviewedFingerprint = (await service.PreviewBalanceEditAsync(edit)).Fingerprint };
        Assert.True((await service.ConfirmBalanceEditAsync(edit)).Success);
        var next = await service.ConfirmAsync(new(Guid.NewGuid(), seed.Date, ProductionDailyArea.Cutting, seed.ShiftId, seed.Products[1], 3, null, "6789"));
        Assert.True(next.Success);
        var reverse = new ReverseProductionDailyCaptureCommand(Guid.NewGuid(), next.Id!.Value, "Correction", "6789");
        Assert.False((await service.ReverseAsync(reverse)).Success);
        Assert.False((await service.ReverseAsync(reverse with { AdminPin = "0123", Reason = "" })).Success);
        Assert.True((await service.ReverseAsync(reverse with { AdminPin = "0123" })).Success);
        Assert.False((await service.ReverseAsync(reverse)).Success);
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var admin = await db.Users.SingleAsync(x => x.RoleId == 1);
        admin.RoleId = 3;
        await db.SaveChangesAsync();
        Assert.False((await service.ConfirmBalanceEditAsync(edit)).Success);
        Assert.False((await service.ReverseAsync(reverse with { AdminPin = "0123" })).Success);
    }

    [Fact]
    public async Task Admin_session_cannot_confirm_production_with_operator_pin_and_matrix_keeps_values()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        await AddUsers(factory.Services);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var login = await client.GetStringAsync("/Admin/Login");
        await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Input.Pin"] = "0123", ["__RequestVerificationToken"] = Input(login, "__RequestVerificationToken") }));
        var page = await client.GetStringAsync($"/Operations/Production?Tab=capture&Day={seed.Date:yyyy-MM-dd}");
        var fields = new Dictionary<string, string>
        {
            ["Group.AllAreas"] = "true",
            ["Group.OperationId"] = Guid.NewGuid().ToString(),
            ["Group.Date"] = seed.Date.ToString("yyyy-MM-dd"),
            ["Group.ShiftId"] = seed.ShiftId.ToString(),
            ["Group.Rows[0].ProductId"] = seed.Products[0].ToString(),
            ["Group.Rows[0].Area"] = "0",
            ["Group.Rows[0].Quantity"] = "7"
        };
        var review = await PostAsync(client, "GroupPreview", page, fields);
        var preview = await review.Content.ReadAsStringAsync();
        fields["Group.Fingerprint"] = Input(preview, "Group.Fingerprint");
        fields["Group.Pin"] = "4567";
        var response = await PostAsync(client, "GroupConfirm", preview, fields);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(RoleAccess.ProductionWarning, html);
        Assert.Contains("alert-warning", html);
        Assert.Equal("7", Input(html, "Group.Rows[0].Quantity"));
        Assert.Equal("", Input(html, "Group.Pin"));
        await Fixture("production-role-warning", await response.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionDailyCaptures.ToListAsync());
    }

    [Fact]
    public async Task Notifications_filter_categories_and_cache_by_audience_and_logout_keeps_public_access()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        await SeedAsync(factory.Services);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var publicSnapshot = await client.GetStringAsync("/Reports/Notifications?handler=Snapshot");
        var login = await client.GetStringAsync("/Admin/Login");
        await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Input.Pin"] = "0123", ["__RequestVerificationToken"] = Input(login, "__RequestVerificationToken") }));
        var adminSnapshot = await client.GetStringAsync("/Reports/Notifications?handler=Snapshot");
        Assert.NotEqual(publicSnapshot, adminSnapshot);
        var home = await client.GetStringAsync("/");
        var logout = await client.PostAsync("/Admin/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = Input(home, "__RequestVerificationToken") }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        var signedOutInventory = await client.GetStringAsync("/Modules/inventory");
        Assert.DoesNotContain("href=\"/Reports/Notifications", signedOutInventory);
        Assert.DoesNotContain("data-notification-trigger", signedOutInventory);
        Assert.DoesNotContain("id=\"operational-notifications\"", signedOutInventory);
        Assert.Equal(publicSnapshot, await client.GetStringAsync("/Reports/Notifications?handler=Snapshot"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/Reports/Notifications?category=AgedWip")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Reports/Notifications?category=BelowMinimum")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Operations/Production")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Production/Schedule")).StatusCode);
    }

    [Fact]
    public async Task Warehouse_rejects_production_pin_even_with_admin_session_and_preserves_form()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        await AddUsers(factory.Services);
        Guid locationId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var location = new Location { Code = "ROLE-RACK", Kind = LocationKind.Rack };
            db.Locations.Add(location); await db.SaveChangesAsync(); locationId = location.Id;
        }
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var login = await client.GetStringAsync("/Admin/Login");
        await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Input.Pin"] = "0123", ["__RequestVerificationToken"] = Input(login, "__RequestVerificationToken") }));
        var page = await client.GetStringAsync("/Operations/Entry");
        var fields = new Dictionary<string, string>
        { ["Input.OperationId"] = Guid.NewGuid().ToString(), ["Input.ProductId"] = seed.Products[0].ToString(), ["Input.DestinationLocationId"] = locationId.ToString(), ["Input.Quantity"] = "7", ["Input.Pin"] = "6789", ["__RequestVerificationToken"] = Input(page, "__RequestVerificationToken") };
        var response = await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(fields));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(RoleAccess.WarehouseWarning, html);
        Assert.Equal("7", Input(html, "Input.Quantity"));
        Assert.Equal("", Input(html, "Input.Pin"));
        await Fixture("warehouse-role-warning", await response.Content.ReadAsStringAsync());
        using var check = factory.Services.CreateScope();
        var database = check.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.Empty(await database.InventoryMovements.ToListAsync());
        var movements = check.ServiceProvider.GetRequiredService<InventoryMovementService>();
        var command = new InventoryMovementCommand(Guid.NewGuid(), InventoryMovementType.Entry, "4567", [new(seed.Products[0], 7, DestinationLocationId: locationId)]);
        Assert.Equal(InventoryMovementStatus.Success, (await movements.ConfirmAsync(command)).Status);
        Assert.Equal(InventoryMovementStatus.RoleNotAllowed, (await movements.ConfirmAsync(command with { Pin = "6789" })).Status);
        Assert.Equal(InventoryMovementStatus.InvalidPin, (await movements.ConfirmAsync(command with { Pin = "9999" })).Status);
        Assert.Single(await database.InventoryMovements.ToListAsync());
        var receiving = check.ServiceProvider.GetRequiredService<ReceivingService>();
        var open = new OpenReceivingDocumentCommand(Guid.NewGuid(), ReceivingDocumentType.PurchaseOrder, "ROLE-PO", "Supplier", seed.Date, null, "4567", [new(seed.Products[0], 2)]);
        Assert.Equal(ReceivingCommandStatus.Success, (await receiving.OpenAsync(open)).Status);
        Assert.Equal(ReceivingCommandStatus.RoleNotAllowed, (await receiving.OpenAsync(open with { Pin = "6789" })).Status);
        Assert.Equal(ReceivingCommandStatus.InvalidPin, (await receiving.OpenAsync(open with { Pin = "9999" })).Status);
        Assert.Single(await database.ReceivingDocuments.ToListAsync());
    }

    private static async Task AddUsers(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var pins = scope.ServiceProvider.GetRequiredService<UserPinService>();
        foreach (var (name, role, pin) in new[] { ("Role operator", (short)2, "4567"), ("Role production", (short)3, "6789") })
        {
            var user = new User { FullName = name, RoleId = role, PinHash = "", PinLookup = "" };
            await pins.AssignAsync(user, pin); db.Users.Add(user);
        }
        await db.SaveChangesAsync();
    }

    private static async Task Fixture(string name, string html)
    {
        var directory = Environment.GetEnvironmentVariable("WAREHOUSE_ROLE_VISUAL_FIXTURES");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".html"), html);
    }
}
