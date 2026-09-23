using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Tests.Production;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionDailyGroupRouteTests
{
    [Fact]
    public async Task Review_requires_no_pin_errors_preserve_rows_and_confirmation_clears_secrets()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Guid weekId, productId, shiftId;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monday = today.AddDays(-14 - ((int)today.DayOfWeek + 6) % 7);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var user = new User { FullName = "Group admin", RoleId = 1, PinHash = "", PinLookup = "" };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
            db.Users.Add(user);
            await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
            var product = new Product { Sku = "GROUP-WEB", BaseUnitId = 1 };
            db.Products.Add(product); await db.SaveChangesAsync(); productId = product.Id;
            var schedule = scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>();
            weekId = (await schedule.CreateWeekAsync(new(Guid.NewGuid(), monday, user.Id))).Id!.Value;
            var week = (await schedule.GetWeekAsync(weekId))!;
            Assert.True((await schedule.SaveLineAsync(new(Guid.NewGuid(), weekId, null, week.Version, null, monday, productId, 20, null, null, null, null, user.Id))).Success);
            week = (await schedule.GetWeekAsync(weekId))!;
            Assert.True((await schedule.PublishAsync(new(Guid.NewGuid(), weekId, week.Version, "0123", user.Id))).Success);
            shiftId = (await db.ProductionDailyConfigurations.SingleAsync()).Shift1Id!.Value;
        }
        var page = await client.GetStringAsync($"/Operations/Production?WeekId={weekId}");
        Assert.Contains("GROUP-WEB", page);
        Assert.Contains("data-daily-product-field", page);
        Assert.DoesNotContain("id=\"group-search\"", page);
        var lookup = await client.GetStringAsync($"/Operations/Production?handler=DailyProducts&date={monday:yyyy-MM-dd}&area=Cutting&q=GROUP");
        using (var lookupJson = System.Text.Json.JsonDocument.Parse(lookup))
            Assert.Equal(productId, lookupJson.RootElement[0].GetProperty("items")[0].GetProperty("id").GetGuid());
        var weeklyPage = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/Production?Tab=balance&WeekId={weekId}&Through={monday:yyyy-MM-dd}"));
        Assert.Contains("Balance diario", weeklyPage);
        Assert.Contains("Pendiente al iniciar", weeklyPage);
        Assert.Contains("Pendiente para T2", weeklyPage);
        Assert.Contains("Pendiente final", weeklyPage);
        Assert.DoesNotContain("Ver días y turnos", weeklyPage);
        Assert.DoesNotContain("Día del detalle", weeklyPage);
        Assert.Contains("data-balance-date", weeklyPage);
        Assert.Contains(">T1</th>", weeklyPage);
        Assert.Contains(">T2</th>", weeklyPage);
        Assert.Contains("colspan=\"5\"", weeklyPage);
        Assert.Contains("production-weekly-product", weeklyPage);
        Assert.Equal(monday.ToString("yyyy-MM-dd"), Input(weeklyPage, "Through"));

        var rtp = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/Production?Day={monday:yyyy-MM-dd}&Area=ReadyToPack&ShiftId={shiftId}"));
        Assert.Contains("No hay pendientes registrados", rtp);
        Assert.Contains("Group.AddProductId", rtp);
        Assert.Contains("Area=Cutting", rtp);
        Assert.DoesNotContain("name=\"Group.Rows[0].Quantity\"", rtp);
        var noMatch = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/Production?WeekId={weekId}&Area=Cutting&Sku=NO-MATCH"));
        Assert.Contains("Ningún producto coincide", noMatch);
        var missing = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/Production?Day={monday.AddDays(-7):yyyy-MM-dd}"));
        Assert.Contains("No hay una semana programada", missing);
        var fields = new Dictionary<string, string>
        {
            ["Group.OperationId"] = Input(page, "Group.OperationId"),
            ["Group.Fingerprint"] = Input(page, "Group.Fingerprint"),
            ["Group.AddProductId"] = Input(page, "Group.AddProductId"),
            ["Group.Date"] = monday.ToString("yyyy-MM-dd"),
            ["Group.Area"] = "Cutting",
            ["Group.ShiftId"] = shiftId.ToString(),
            ["Group.Rows[0].ProductId"] = productId.ToString(),
            ["Group.Rows[0].Sku"] = "GROUP-WEB",
            ["Group.Rows[0].Quantity"] = "3,5",
            ["Group.Rows[0].Notes"] = "Keep this note"
        };
        var invalid = await Post("GroupPreview", page, fields);
        Assert.Equal("3,5", Input(invalid, "Group.Rows[0].Quantity"));
        Assert.Equal("Keep this note", Input(invalid, "Group.Rows[0].Notes"));
        fields["Sku"] = "NO-MATCH";
        var filtered = await Post("GroupFilter", invalid, fields);
        Assert.Equal("3,5", Input(filtered, "Group.Rows[0].Quantity"));
        Assert.Equal("Keep this note", Input(filtered, "Group.Rows[0].Notes"));
        Guid extraProduct;
        using (var addScope = factory.Services.CreateScope())
        {
            var addDb = addScope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var product = new Product { Sku = "UNPLANNED-WEB", BaseUnitId = 1 };
            addDb.Products.Add(product); await addDb.SaveChangesAsync(); extraProduct = product.Id;
        }
        fields["Group.AddProductId"] = extraProduct.ToString();
        var addedProduct = await Post("GroupAdd", filtered, fields);
        Assert.Equal("3,5", Input(addedProduct, "Group.Rows[0].Quantity"));
        Assert.Equal(extraProduct.ToString(), Input(addedProduct, "Group.Rows[1].ProductId"));
        Assert.Contains("UNPLANNED-WEB", addedProduct);
        var addedAgain = await Post("GroupAdd", addedProduct, new Dictionary<string, string>(fields) {
            ["Group.Rows[1].ProductId"] = extraProduct.ToString(), ["Group.Rows[1].Quantity"] = "", ["Group.Rows[1].Notes"] = ""
        });
        Assert.DoesNotContain("name=\"Group.Rows[2].ProductId\"", addedAgain);
        Assert.Equal("3,5", Input(addedAgain, "Group.Rows[0].Quantity"));
        fields.Remove("Sku"); fields.Remove("Group.AddProductId");
        fields["Group.Rows[0].Quantity"] = "2";
        fields["Group.Pin"] = "";
        var reviewed = await Post("GroupPreview", invalid, fields);
        Assert.Contains("data-group-confirm", reviewed);
        fields["Group.Fingerprint"] = Input(reviewed, "Group.Fingerprint");
        fields["Group.Pin"] = "";
        var missingPin = await Post("GroupConfirm", reviewed, fields);
        Assert.Contains("NIP inválido", WebUtility.HtmlDecode(missingPin));
        Assert.Equal("2", Input(missingPin, "Group.Rows[0].Quantity"));
        fields["Group.Pin"] = "9876";
        var badPin = await Post("GroupConfirm", reviewed, fields);
        Assert.Equal("2", Input(badPin, "Group.Rows[0].Quantity"));
        Assert.Equal("", Input(badPin, "Group.Pin"));
        Assert.DoesNotContain("9876", badPin);
        fields["Group.Pin"] = "0123";
        fields["Group.Fingerprint"] = Input(badPin, "Group.Fingerprint");
        fields["__RequestVerificationToken"] = Input(badPin, "__RequestVerificationToken");
        var confirmed = await client.PostAsync("/Operations/Production?handler=GroupConfirm", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);
        var cleared = await client.GetStringAsync(confirmed.Headers.Location);
        Assert.Equal("", Input(cleared, "Group.Rows[0].Quantity"));
        Assert.DoesNotContain("name=\"Group.Pin\"", cleared);
        Assert.Equal(monday.ToString("yyyy-MM-dd"), Input(cleared, "Day"));
        using var verify = factory.Services.CreateScope();
        var context = verify.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.Equal(2, Assert.Single(await context.ProductionDailyCaptures.ToListAsync()).Quantity);
        var captureService = verify.ServiceProvider.GetRequiredService<ProductionDailyCaptureService>();
        Assert.Equal(18, (await captureService.GetAvailabilityAsync(monday, ProductionDailyArea.Cutting)).Single(x => x.ProductId == productId).Available);
        Assert.Equal(2, (await captureService.GetAvailabilityAsync(monday, ProductionDailyArea.Sewing)).Single(x => x.ProductId == productId).Available);
        var sewingPage = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/Production?Day={monday:yyyy-MM-dd}&Area=Sewing&ShiftId={shiftId}"));
        Assert.Contains("GROUP-WEB", sewingPage);
        Assert.Matches("production-entry__pending[^>]*>2(?:<|\\s)", sewingPage);
        Assert.Single(await context.ProductionCaptureSubmissions.ToListAsync());

        var balanceHtml = await client.GetStringAsync($"/Operations/Production?Tab=balance&WeekId={weekId}&Through={monday:yyyy-MM-dd}");
        Assert.Contains("balance-edit-cell", balanceHtml);
        var editId = Guid.NewGuid();
        async Task<JsonDocument> EditPost(string handler, string fingerprint, string pin, string requested = "5")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/Operations/Production?handler=" + handler);
            request.Headers.Add("RequestVerificationToken", Input(balanceHtml, "__RequestVerificationToken"));
            request.Content = JsonContent.Create(new { operationId = editId, weekId, date = monday, reason = (string?)null, fingerprint, pin,
                cells = new[] { new { productId, area = 0, shift = 1, observed = "2", requested } } });
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        using (var invalidEdit = await EditPost("BalanceEditPreview", "", "", ""))
            Assert.False(invalidEdit.RootElement.GetProperty("canConfirm").GetBoolean());
        using var editReview = await EditPost("BalanceEditPreview", "", "");
        Assert.True(editReview.RootElement.GetProperty("canConfirm").GetBoolean(), editReview.RootElement.ToString());
        Assert.Single(await context.ProductionDailyCaptures.ToListAsync());
        var editFingerprint = editReview.RootElement.GetProperty("fingerprint").GetString()!;
        using (var invalidPin = await EditPost("BalanceEditConfirm", editFingerprint, "9876"))
            Assert.False(invalidPin.RootElement.GetProperty("success").GetBoolean());
        using (var savedEdit = await EditPost("BalanceEditConfirm", editFingerprint, "0123"))
            Assert.True(savedEdit.RootElement.GetProperty("success").GetBoolean(), savedEdit.RootElement.ToString());
        Assert.Equal(5, await context.ProductionDailyCaptures.Where(x => x.Status == ProductionDailyCaptureStatus.Active).SumAsync(x => x.Quantity));

        var login = await client.GetStringAsync("/Admin/Login");
        var signedIn = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["__RequestVerificationToken"] = Input(login, "__RequestVerificationToken"), ["Input.Pin"] = "0123" }));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        var admin = await context.Users.SingleAsync(x => x.FullName == "Group admin");
        var scheduleService = verify.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>();
        var targetId = (await scheduleService.CreateWeekAsync(new(Guid.NewGuid(), monday.AddDays(7), admin.Id))).Id!.Value;
        var targetPage = await client.GetStringAsync($"/Admin/Production/Schedule?WeekId={targetId}");
        var draftCapture = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/Production?WeekId={targetId}"));
        Assert.Contains("Esta semana está en borrador", draftCapture);
        Assert.DoesNotContain("name=\"Group.Rows[0].Quantity\"", draftCapture);
        Assert.Equal("", Input(targetPage, "Copy.Rows[0].Quantity"));
        Assert.Equal(monday.AddDays(7).ToString("yyyy-MM-dd"), Input(targetPage, "Copy.Rows[0].Date"));
        var copyFields = new Dictionary<string, string>();
        foreach (var name in new[] { "OperationId", "WeekId", "ExpectedVersion", "SourceWeekId", "SourceVersion", "Rows[0].SourceLineId", "Rows[0].Date", "Rows[0].Sku" })
            copyFields["Copy." + name] = Input(targetPage, "Copy." + name);
        copyFields["Copy.Rows[0].Selected"] = "true";
        copyFields["Copy.Rows[0].Quantity"] = "1,5";
        copyFields["__RequestVerificationToken"] = Input(targetPage, "__RequestVerificationToken");
        var invalidCopy = await client.PostAsync("/Admin/Production/Schedule?handler=Copy", new FormUrlEncodedContent(copyFields));
        Assert.Equal(HttpStatusCode.OK, invalidCopy.StatusCode);
        var copyError = await invalidCopy.Content.ReadAsStringAsync();
        Assert.Equal("1,5", Input(copyError, "Copy.Rows[0].Quantity"));
        Assert.False(await context.ProductionScheduleLines.AnyAsync(x => x.WeekId == targetId));
        copyFields["Copy.Rows[0].Quantity"] = "5";
        copyFields["__RequestVerificationToken"] = Input(copyError, "__RequestVerificationToken");
        var savedCopy = await client.PostAsync("/Admin/Production/Schedule?handler=Copy", new FormUrlEncodedContent(copyFields));
        Assert.Equal(HttpStatusCode.Redirect, savedCopy.StatusCode);
        var copied = await context.ProductionScheduleLines.SingleAsync(x => x.WeekId == targetId);
        Assert.Equal(5, copied.Quantity);
        Assert.Null(copied.Notes);
        Assert.Null(copied.WorkOrderId);

        async Task<string> Post(string handler, string html, Dictionary<string, string> values)
        {
            values["__RequestVerificationToken"] = Input(html, "__RequestVerificationToken");
            var response = await client.PostAsync("/Operations/Production?handler=" + handler, new FormUrlEncodedContent(values));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }
    }

    private static string Input(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html,
        "<input(?=[^>]*name=\"" + Regex.Escape(name) + "\")[^>]*value=\"([^\"]*)\"").Groups[1].Value);
}
