using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using static WarehouseEPI.Tests.Web.ProductionCaptureRecoveryTests;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionMatrixCaptureTests
{
    [Fact]
    public async Task Concurrent_capture_or_closed_week_requires_a_new_review_without_partial_registration()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ProductionDailyCaptureService>();
        var command = new ProductionMatrixCommand(Guid.NewGuid(), seed.Date, seed.ShiftId,
            Enum.GetValues<ProductionDailyArea>().Select(area => new ProductionMatrixRow(seed.Products[0], area, 2, null)).ToArray(), Pin: "0123");
        var preview = await service.PreviewMatrixAsync(command);
        Assert.True(preview.CanConfirm);
        var concurrent = command with { OperationId = Guid.NewGuid(), Rows = [command.Rows[0]] };
        var concurrentPreview = await service.PreviewMatrixAsync(concurrent);
        Assert.True((await service.ConfirmMatrixAsync(concurrent with { ReviewedFingerprint = concurrentPreview.Fingerprint })).Success);
        Assert.Equal(ProductionDailyCommandStatus.ConcurrencyConflict, (await service.ConfirmMatrixAsync(command with { ReviewedFingerprint = preview.Fingerprint })).Status);
        Assert.Single(await db.ProductionDailyCaptures.ToListAsync());
        preview = await service.PreviewMatrixAsync(command);
        var week = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == seed.WeekId);
        week.Status = ProductionScheduleWeekStatus.Closed;
        await db.SaveChangesAsync();
        Assert.False((await service.ConfirmMatrixAsync(command with { ReviewedFingerprint = preview.Fingerprint })).Success);
        Assert.Single(await db.ProductionDailyCaptures.ToListAsync());
    }

    [Fact]
    public async Task Hundred_products_with_three_areas_fit_the_form_limit()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var seed = await SeedAsync(factory.Services);
        var page = await client.GetStringAsync($"/Operations/Production?Tab=capture&Day={seed.Date:yyyy-MM-dd}");
        var fields = new Dictionary<string, string> { ["Group.AllAreas"] = "true", ["Group.OperationId"] = Guid.NewGuid().ToString(), ["Group.Date"] = $"{seed.Date:yyyy-MM-dd}", ["Group.ShiftId"] = seed.ShiftId.ToString() };
        for (var product = 0; product < 100; product++)
        {
            var id = Guid.NewGuid();
            for (var area = 0; area < 3; area++)
            {
                var prefix = $"Group.Rows[{product * 3 + area}]";
                fields[prefix + ".ProductId"] = id.ToString(); fields[prefix + ".Area"] = area.ToString();
                fields[prefix + ".Quantity"] = "0"; fields[prefix + ".Notes"] = ""; fields[prefix + ".Sku"] = "untrusted";
            }
        }
        var response = await PostAsync(client, "GroupRestore", page, fields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(300, System.Text.RegularExpressions.Regex.Count(html, "data-group-quantity"));
        Assert.DoesNotContain("value=\"untrusted\"", html);
    }

    [Fact]
    public async Task Three_areas_restore_review_confirm_and_lost_response_have_one_receipt()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var seed = await SeedAsync(factory.Services);
        using (var seedScope = factory.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            (await seedDb.Products.SingleAsync(x => x.Id == seed.Products[2])).Sku = "RECOVER-003-SKU-LARGO-PARA-REVISION-DE-TRES-AREAS";
            await seedDb.SaveChangesAsync();
        }
        var page = await client.GetStringAsync($"/Operations/Production?Tab=capture&Day={seed.Date:yyyy-MM-dd}&ShiftId={seed.ShiftId}");
        await Fixture("matrix-capture", page);
        Assert.Contains("data-capture-week", page);
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Count(page, "data-capture-day="));
        Assert.DoesNotContain("id=\"group-area\"", page);
        Assert.DoesNotContain("type=\"date\"", page);
        var fields = new Dictionary<string, string>
        {
            ["Group.AllAreas"] = "true",
            ["Group.OperationId"] = Input(page, "Group.OperationId"),
            ["Group.Date"] = $"{seed.Date:yyyy-MM-dd}",
            ["Group.ShiftId"] = seed.ShiftId.ToString(),
            ["Group.Mode"] = "list"
        };
        var entries = new[] { (0, 0, "4"), (0, 1, "3"), (0, 2, "2"), (1, 1, "6"), (2, 2, "8") };
        for (var i = 0; i < entries.Length; i++)
        {
            var (product, area, quantity) = entries[i];
            fields[$"Group.Rows[{i}].ProductId"] = seed.Products[product].ToString();
            fields[$"Group.Rows[{i}].Area"] = area.ToString();
            fields[$"Group.Rows[{i}].Quantity"] = quantity;
            fields[$"Group.Rows[{i}].Notes"] = $"note {i}";
        }
        fields["Group.Rows[1].Quantity"] = "3,5";
        var restored = await PostAsync(client, "GroupRestore", page, fields);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var html = await restored.Content.ReadAsStringAsync();
        Assert.Equal("3,5", Input(html, "Group.Rows[1].Quantity"));
        Assert.Equal("6", Input(html, "Group.Rows[4].Quantity"));
        Assert.Equal("note 3", Input(html, "Group.Rows[4].Notes"));
        Assert.Equal("8", Input(html, "Group.Rows[8].Quantity"));
        Assert.Equal("", Input(html, "Group.Fingerprint"));
        fields["Group.Rows[1].Quantity"] = "3";
        var review = await PostAsync(client, "GroupPreview", html, fields);
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        html = await review.Content.ReadAsStringAsync();
        await Fixture("matrix-review", html);
        Assert.Contains("data-group-confirm", html);
        fields["Group.Fingerprint"] = Input(html, "Group.Fingerprint");
        Assert.NotEmpty(fields["Group.Fingerprint"]);
        fields["Group.Pin"] = "9999";
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "GroupConfirm", html, fields)).StatusCode);
        fields["Group.Pin"] = "0123";
        var saved = await PostAsync(client, "GroupConfirm", html, fields);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.Equal(5, await db.ProductionDailyCaptures.CountAsync());
        Assert.Equal(23m, await db.ProductionDailyCaptures.SumAsync(x => x.Quantity));
        Assert.Single(await db.ProductionCaptureSubmissions.ToListAsync());
        Assert.Equal(3, await db.ProductionDailyCaptures.Where(x => x.ProductId == seed.Products[0]).Select(x => x.Area).Distinct().CountAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(client, "GroupConfirm", html, fields)).StatusCode);
        fields.Remove("Group.Pin"); fields.Remove("Group.Fingerprint");
        var receipt = await PostAsync(client, "GroupRestore", html, fields);
        Assert.Equal(saved.Headers.Location, receipt.Headers.Location);
        var proof = WebUtility.HtmlDecode(await client.GetStringAsync(receipt.Headers.Location));
        Assert.Contains("Costura", proof); Assert.Contains("Ready to Pack", proof);
        Assert.Contains($"data-receipt-operation=\"{fields["Group.OperationId"]}\"", proof);
        fields["Group.Rows[0].Quantity"] = "999";
        var conflict = await PostAsync(client, "GroupRestore", proof, fields);
        Assert.Contains("data-restore-conflict=\"true\"", await conflict.Content.ReadAsStringAsync());
        Assert.Equal(5, await db.ProductionDailyCaptures.CountAsync());
        var week = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == seed.WeekId);
        week.Status = ProductionScheduleWeekStatus.Closed; await db.SaveChangesAsync();
        fields["Group.Rows[0].Quantity"] = "4";
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(client, "GroupRestore", proof, fields)).StatusCode);
        var closed = await client.GetStringAsync($"/Operations/Production?Tab=capture&Day={seed.Date:yyyy-MM-dd}&ShiftId={seed.ShiftId}");
        await Fixture("matrix-closed", closed);
        Assert.DoesNotContain("data-group-quantity", closed);
    }

    [Fact]
    public async Task Legacy_draft_import_preserves_area_and_uses_a_new_matrix_operation()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var seed = await SeedAsync(factory.Services);
        var page = await client.GetStringAsync($"/Operations/Production?Tab=capture&Day={seed.Date:yyyy-MM-dd}&ShiftId={seed.ShiftId}");
        var operation = Guid.NewGuid();
        var fields = new Dictionary<string, string>
        {
            ["Group.OperationId"] = operation.ToString(),
            ["Group.Date"] = $"{seed.Date:yyyy-MM-dd}",
            ["Group.ShiftId"] = seed.ShiftId.ToString(),
            ["Group.Area"] = "Sewing",
            ["Group.ImportLegacy"] = "true",
            ["Group.Rows[0].ProductId"] = seed.Products[0].ToString(),
            ["Group.Rows[0].Quantity"] = "4,2",
            ["Group.Rows[0].Notes"] = "old note"
        };
        var response = await PostAsync(client, "GroupRestore", page, fields);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("true", Input(html, "Group.AllAreas").ToLowerInvariant());
        Assert.NotEqual(operation.ToString(), Input(html, "Group.OperationId"));
        Assert.Equal(operation.ToString(), Input(html, "Group.LegacyOperationId"));
        Assert.Equal("", Input(html, "Group.Rows[0].Quantity"));
        Assert.Equal("4,2", Input(html, "Group.Rows[1].Quantity"));
        Assert.Equal("old note", Input(html, "Group.Rows[1].Notes"));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("area")]
    [InlineData("notes")]
    [InlineData("products")]
    public async Task Matrix_rejects_invalid_shape_without_any_writes(string scenario)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var seed = await SeedAsync(factory.Services);
        var page = await client.GetStringAsync($"/Operations/Production?Tab=capture&Day={seed.Date:yyyy-MM-dd}");
        var fields = new Dictionary<string, string> { ["Group.AllAreas"] = "true", ["Group.OperationId"] = Guid.NewGuid().ToString(), ["Group.Date"] = $"{seed.Date:yyyy-MM-dd}", ["Group.ShiftId"] = seed.ShiftId.ToString() };
        for (var i = 0; i < (scenario == "products" ? 101 : 2); i++)
        {
            fields[$"Group.Rows[{i}].ProductId"] = (scenario == "products" ? Guid.NewGuid() : seed.Products[0]).ToString();
            fields[$"Group.Rows[{i}].Area"] = scenario == "duplicate" ? "0" : scenario == "area" ? "99" : (i % 3).ToString();
            fields[$"Group.Rows[{i}].Quantity"] = "2";
        }
        if (scenario == "notes") fields["Group.Rows[0].Notes"] = new string('x', 501);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "GroupRestore", page, fields)).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionDailyCaptures.AnyAsync());
    }

    private static async Task Fixture(string name, string html)
    {
        var directory = Environment.GetEnvironmentVariable("WAREHOUSE_CAPTURE_VISUAL_FIXTURES");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); await File.WriteAllTextAsync(Path.Combine(directory, name + ".html"), html);
    }
}
