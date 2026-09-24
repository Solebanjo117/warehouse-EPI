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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_totals_include_only_active_records_for_the_selected_day_area_and_shift(bool future)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        Guid weekId, productId, secondProductId, shift1;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monday = today.AddDays((future ? 14 : -14) - ((int)today.DayOfWeek + 6) % 7);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var user = new User { FullName = "Totals operator", RoleId = 1, PinHash = "", PinLookup = "" };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
            db.Users.Add(user);
            await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
            var product = new Product { Sku = "TOTAL-WEB", Description = "Finished part", BaseUnitId = 1 };
            var secondProduct = new Product { Sku = "ZZZ-WEB", BaseUnitId = 1 };
            db.Products.AddRange(product, secondProduct);
            await db.SaveChangesAsync();
            productId = product.Id;
            secondProductId = secondProduct.Id;
            var schedule = scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>();
            weekId = (await schedule.CreateWeekAsync(new(Guid.NewGuid(), monday, user.Id))).Id!.Value;
            var week = (await schedule.GetWeekAsync(weekId))!;
            Assert.True((await schedule.SaveLineAsync(new(Guid.NewGuid(), weekId, null, week.Version, null,
                monday, productId, 150, null, null, null, null, user.Id))).Success);
            week = (await schedule.GetWeekAsync(weekId))!;
            Assert.True((await schedule.SaveLineAsync(new(Guid.NewGuid(), weekId, null, week.Version, null,
                monday, secondProductId, 10, null, null, null, null, user.Id))).Success);
            week = (await schedule.GetWeekAsync(weekId))!;
            Assert.True((await schedule.PublishAsync(new(Guid.NewGuid(), weekId, week.Version, "0123", user.Id))).Success);
            var setup = await db.ProductionDailyConfigurations.SingleAsync();
            shift1 = setup.Shift1Id!.Value;
            void Seed(decimal quantity, DateOnly date, ProductionDailyArea area, Guid shift, ProductionDailyCaptureStatus status)
            {
                db.ProductionDailyCaptures.Add(new ProductionDailyCapture
                {
                    OperationId = Guid.NewGuid(), RequestFingerprint = Guid.NewGuid().ToString(),
                    WeekId = weekId, EffectiveDate = date, Area = area,
                    StageId = area == ProductionDailyArea.Cutting ? setup.CuttingStageId!.Value : setup.SewingStageId!.Value,
                    ShiftId = shift, ProductId = productId, Quantity = quantity, ResponsibleUserId = user.Id,
                    RecordedAt = DateTimeOffset.UtcNow, Status = status
                });
            }
            Seed(80, monday, ProductionDailyArea.Cutting, shift1, ProductionDailyCaptureStatus.Active);
            Seed(20, monday, ProductionDailyArea.Cutting, shift1, ProductionDailyCaptureStatus.Active);
            Seed(50, monday, ProductionDailyArea.Cutting, setup.Shift2Id!.Value, ProductionDailyCaptureStatus.Active);
            Seed(40, monday.AddDays(1), ProductionDailyArea.Cutting, shift1, ProductionDailyCaptureStatus.Active);
            Seed(30, monday, ProductionDailyArea.Sewing, shift1, ProductionDailyCaptureStatus.Active);
            Seed(500, monday, ProductionDailyArea.Cutting, shift1, ProductionDailyCaptureStatus.Reversed);
            await db.SaveChangesAsync();
        }
        var page = WebUtility.HtmlDecode(await client.GetStringAsync(
            $"/Operations/Production?Day={monday:yyyy-MM-dd}&Area=Cutting&ShiftId={shift1}"));
        Assert.Contains("data-registered=\"100\"", page);
        Assert.DoesNotMatch("<input[^>]*id=\"group-date\"[^>]*max=", page);
        Assert.DoesNotContain("Finished part", page);
        Assert.Equal(secondProductId.ToString(), Input(page, "Group.Rows[1].ProductId"));
        var fields = new Dictionary<string, string>
        {
            ["Group.OperationId"] = Input(page, "Group.OperationId"),
            ["Group.Date"] = monday.ToString("yyyy-MM-dd"), ["Group.Area"] = "Cutting",
            ["Group.ShiftId"] = shift1.ToString(),
            ["Group.Rows[0].ProductId"] = productId.ToString(),
            ["Group.Rows[0].Sku"] = "TOTAL-WEB", ["Group.Rows[0].Quantity"] = "20",
            ["__RequestVerificationToken"] = Input(page, "__RequestVerificationToken")
        };
        var response = await client.PostAsync("/Operations/Production?handler=GroupPreview", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reviewed = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("Total resultante · vista previa", reviewed);
        Assert.Contains("<dt>Total resultante · vista previa</dt><dd>120</dd>", reviewed);
        fields["Group.Mode"] = "list";
        fields["Group.Rows[0].Notes"] = "Keep this note";
        var modeResponse = await client.PostAsync("/Operations/Production?handler=GroupMode", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, modeResponse.StatusCode);
        var listed = await modeResponse.Content.ReadAsStringAsync();
        Assert.Equal("20", Input(listed, "Group.Rows[0].Quantity"));
        Assert.Equal("Keep this note", Input(listed, "Group.Rows[0].Notes"));
        Assert.Equal(secondProductId.ToString(), Input(listed, "Group.Rows[1].ProductId"));
        if (future)
        {
            var balance = WebUtility.HtmlDecode(await client.GetStringAsync(
                $"/Operations/Production?Tab=balance&WeekId={weekId}&Through={monday:yyyy-MM-dd}"));
            Assert.Contains("La programación futura es una proyección.", balance);
            Assert.Contains("data-edit-area=", balance);
            Assert.DoesNotContain("no representa producción realizada", balance);
        }
    }

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
            var product = new Product { Sku = "GROUP-WEB", Description = "Hidden program description", BaseUnitId = 1 };
            product.Barcodes.Add(new ProductBarcode { Barcode = "GROUP-WEB-BARCODE", IsActive = true });
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
        foreach (var code in new[] { "GROUP-WEB", "GROUP-WEB-BARCODE" })
        {
            var exact = await client.GetFromJsonAsync<JsonElement>($"/Operations/Lookup?handler=ResolveProduct&code={code}");
            Assert.Equal(productId, exact.GetProperty("id").GetGuid());
            Assert.Equal("GROUP-WEB", exact.GetProperty("sku").GetString());
        }
        var weeklyPage = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/Production?Tab=balance&WeekId={weekId}&Through={monday:yyyy-MM-dd}"));
        Assert.Contains("Balance diario", weeklyPage);
        Assert.Contains("Cierre semanal", weeklyPage);
        Assert.Contains("<details id=\"shift-comparison-heading\"", weeklyPage);
        Assert.Contains("Comparación de turnos", weeklyPage);
        Assert.Contains("data-balance-status", weeklyPage);
        Assert.Contains("Pendiente para próxima semana", weeklyPage);
        Assert.Contains("Cumplimiento semanal por producto y área", weeklyPage);
        Assert.Contains("<details id=\"next-week-heading\"", weeklyPage);
        Assert.Contains("<details id=\"completion-heading\"", weeklyPage);
        Assert.Contains("Resumen semanal de producción por SKU", weeklyPage);
        Assert.Contains("<details id=\"part-summary-heading\"", weeklyPage);
        var summarySection = weeklyPage[weeklyPage.IndexOf("<details id=\"part-summary-heading\"", StringComparison.Ordinal)..];
        var summaryFooter = Regex.Match(summarySection, "<tfoot>(.*?)</tfoot>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Single(Regex.Matches(summaryFooter, "<tr>"));
        Assert.Contains("colspan=\"2\">Total filtrado", summaryFooter);
        Assert.DoesNotContain("Restante según Excel", weeklyPage);
        var expandedWeekly = await client.GetStringAsync($"/Operations/Production?Tab=balance&WeekId={weekId}&Through={monday:yyyy-MM-dd}&WeeklySection=completion");
        Assert.Matches("<details id=\"completion-heading\"[^>]*open=\"open\"", expandedWeekly);
        var expandedSummary = await client.GetStringAsync($"/Operations/Production?Tab=balance&WeekId={weekId}&Through={monday:yyyy-MM-dd}&WeeklySection=summary");
        Assert.Matches("<details id=\"part-summary-heading\"[^>]*open=\"open\"", expandedSummary);
        Assert.Contains($"Corte al domingo {monday.AddDays(6):dd/MM/yyyy}", weeklyPage);
        Assert.Contains("Cifras provisionales", weeklyPage);
        var clampedPages = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/Production?Tab=balance&WeekId={weekId}&Through={monday:yyyy-MM-dd}&PendingPage=999&CompletionPage=999"));
        Assert.Contains("Página 1 de 1", clampedPages);
        Assert.Contains("Pendiente al iniciar", weeklyPage);
        Assert.Contains("Pendiente para T2", weeklyPage);
        Assert.Contains("Pendiente final", weeklyPage);
        Assert.DoesNotContain("Ver días y turnos", weeklyPage);
        Assert.DoesNotContain("Día del detalle", weeklyPage);
        Assert.Contains("data-balance-day", weeklyPage);
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
        Assert.DoesNotContain("The Tab field is required.", invalid);
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
        Assert.Equal("Cutting", Input(cleared, "Group.Area"));
        Assert.Equal(shiftId.ToString(), Input(cleared, "Group.ShiftId"));
        Assert.Contains("Tanda registrada", cleared);
        Assert.Contains("Group admin", cleared);
        Assert.Contains("Ver registro", cleared);
        Assert.Contains("data-registered=\"2\"", cleared);
        Assert.Contains("GROUP-WEB", cleared);
        var retried = await client.PostAsync("/Operations/Production?handler=GroupConfirm", new FormUrlEncodedContent(fields));
        Assert.Equal(confirmed.Headers.Location, retried.Headers.Location);
        using var verify = factory.Services.CreateScope();
        var context = verify.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.Equal(2, Assert.Single(await context.ProductionDailyCaptures.ToListAsync()).Quantity);
        var captureService = verify.ServiceProvider.GetRequiredService<ProductionDailyCaptureService>();
        Assert.Equal(18, (await captureService.GetAvailabilityAsync(monday, ProductionDailyArea.Cutting)).Single(x => x.ProductId == productId).Available);
        Assert.Equal(2, (await captureService.GetAvailabilityAsync(monday, ProductionDailyArea.Sewing)).Single(x => x.ProductId == productId).Available);
        var sewingPage = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/Production?Day={monday:yyyy-MM-dd}&Area=Sewing&ShiftId={shiftId}"));
        Assert.Contains("GROUP-WEB", sewingPage);
        Assert.Contains("Pendiente registrado", sewingPage);
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
        Assert.Contains("part-summary-heading", editReview.RootElement.GetProperty("weeklyHtml").GetString());
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
        balanceHtml = await client.GetStringAsync($"/Operations/Production?Tab=balance&WeekId={weekId}&Through={monday:yyyy-MM-dd}");
        Assert.Contains("data-plan-line=", balanceHtml);
        Assert.DoesNotContain("data-plan-editor", balanceHtml);
        var planLines = await client.GetFromJsonAsync<JsonElement>($"/Operations/Production?handler=BalancePlanLines&weekId={weekId}&date={monday:yyyy-MM-dd}&productId={productId}");
        Assert.Equal(1, planLines.GetArrayLength());
        var planLine = planLines[0];
        using (var planRequest = new HttpRequestMessage(HttpMethod.Post, "/Operations/Production?handler=BalanceEditPreview"))
        {
            planRequest.Headers.Add("RequestVerificationToken", Input(balanceHtml, "__RequestVerificationToken"));
            planRequest.Content = JsonContent.Create(new { operationId = Guid.NewGuid(), weekId, date = monday,
                cells = Array.Empty<object>(), planChanges = new[] { new { lineId = planLine.GetProperty("lineId").GetGuid(),
                    observed = "20", requested = "22", expectedLineVersion = planLine.GetProperty("lineVersion").GetUInt32(),
                    expectedWeekVersion = planLine.GetProperty("weekVersion").GetUInt32() } } });
            var planResponse = await client.SendAsync(planRequest);
            Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);
            using var planReview = JsonDocument.Parse(await planResponse.Content.ReadAsStringAsync());
            Assert.True(planReview.RootElement.GetProperty("canConfirm").GetBoolean(), planReview.RootElement.ToString());
            Assert.True(planReview.RootElement.GetProperty("requiresAdmin").GetBoolean());
            Assert.False(planReview.RootElement.GetProperty("requiresReason").GetBoolean());
            Assert.Contains("part-summary-heading", planReview.RootElement.GetProperty("weeklyHtml").GetString());
        }
        var zeroHtml = await client.GetStringAsync($"/Operations/Production?Tab=balance&WeekId={weekId}&Through={monday.AddDays(6):yyyy-MM-dd}");
        Assert.Contains("data-new-plan=", zeroHtml);
        using (var newPlanRequest = new HttpRequestMessage(HttpMethod.Post, "/Operations/Production?handler=BalanceEditPreview"))
        {
            newPlanRequest.Headers.Add("RequestVerificationToken", Input(zeroHtml, "__RequestVerificationToken"));
            newPlanRequest.Content = JsonContent.Create(new { operationId = Guid.NewGuid(), weekId, date = monday.AddDays(6),
                cells = Array.Empty<object>(), newPlans = new[] { new { operationId = Guid.NewGuid(), productId,
                    requested = "15", expectedWeekVersion = planLine.GetProperty("weekVersion").GetUInt32() } } });
            using var response = await client.SendAsync(newPlanRequest);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("canConfirm").GetBoolean(), json.RootElement.ToString());
            Assert.True(json.RootElement.GetProperty("requiresAdmin").GetBoolean());
            Assert.Equal(1, json.RootElement.GetProperty("newPlans").GetArrayLength());
            Assert.Equal(15, json.RootElement.GetProperty("balance").GetProperty("products")[0].GetProperty("planned").GetDecimal());
        }
        var schedulePage = await client.GetStringAsync($"/Admin/Production/Schedule?WeekId={weekId}&SelectedDay={monday:yyyy-MM-dd}");
        Assert.Contains("GROUP-WEB", schedulePage);
        Assert.DoesNotContain("Hidden program description", schedulePage);
        var weeklyPlanPage = WebUtility.HtmlDecode(await client.GetStringAsync($"/Admin/Production/Schedule?WeekId={weekId}&View=week"));
        Assert.Contains("Productos de la semana", weeklyPlanPage);
        Assert.Contains("GROUP-WEB", weeklyPlanPage);
        Assert.Contains("1 renglón programado", weeklyPlanPage);
        Assert.DoesNotContain("Hidden program description", weeklyPlanPage);
        var admin = await context.Users.SingleAsync(x => x.FullName == "Group admin");
        var scheduleService = verify.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>();
        var targetId = (await scheduleService.CreateWeekAsync(new(Guid.NewGuid(), monday.AddDays(7), admin.Id))).Id!.Value;
        var targetPage = await client.GetStringAsync($"/Admin/Production/Schedule?WeekId={targetId}&ActionPanel=copy");
        foreach (var offset in Enumerable.Range(0, 7))
        {
            var day = monday.AddDays(7 + offset);
            var iso = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var dayLink = Regex.Matches(targetPage, "<a[^>]*flex-shrink-0[^>]*>")
                .Select(match => Regex.Match(match.Value, "href=\"([^\"]+)\"").Groups[1].Value)
                .Select(href => WebUtility.HtmlDecode(href)!)
                .Single(href => href.Contains($"SelectedDay={iso}", StringComparison.Ordinal));
            var selectedPage = WebUtility.HtmlDecode(await client.GetStringAsync(dayLink));
            Assert.Contains($"{day:dd/MM}</h3>", selectedPage);
            var activeDay = Assert.Single(Regex.Matches(selectedPage, "<a[^>]*aria-current=\"date\"[^>]*>").Cast<Match>());
            Assert.Contains($"SelectedDay={iso}", activeDay.Value);
        }
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
        var copiedPage = await client.GetStringAsync($"/Admin/Production/Schedule?WeekId={targetId}&SelectedDay={monday.AddDays(7):yyyy-MM-dd}");
        var deleteLink = Regex.Matches(copiedPage, "href=\"([^\"]+)\"").Cast<Match>()
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)!)
            .Single(href => href.Contains($"DeleteLineId={copied.Id}", StringComparison.Ordinal));
        var confirmation = await client.GetStringAsync(deleteLink);
        Assert.Contains("Confirmar eliminación", confirmation);
        var deletion = new Dictionary<string, string>
        {
            ["DeleteLine.OperationId"] = Input(confirmation, "DeleteLine.OperationId"),
            ["DeleteLine.WeekId"] = Input(confirmation, "DeleteLine.WeekId"),
            ["DeleteLine.LineId"] = Input(confirmation, "DeleteLine.LineId"),
            ["DeleteLine.ExpectedWeekVersion"] = Input(confirmation, "DeleteLine.ExpectedWeekVersion"),
            ["DeleteLine.ExpectedLineVersion"] = Input(confirmation, "DeleteLine.ExpectedLineVersion"),
            ["SelectedDay"] = Input(confirmation, "SelectedDay"),
            ["PageNumber"] = Input(confirmation, "PageNumber"),
            ["__RequestVerificationToken"] = Input(confirmation, "__RequestVerificationToken")
        };
        var deleted = await client.PostAsync("/Admin/Production/Schedule?handler=CancelLine",
            new FormUrlEncodedContent(deletion));
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.True((await context.ProductionScheduleLines.AsNoTracking().SingleAsync(x => x.Id == copied.Id)).IsCancelled);

        var addPage = await client.GetStringAsync($"/Admin/Production/Schedule?WeekId={targetId}&AddLine=true");
        Assert.Contains("data-product-resolve-url", addPage);
        Assert.Contains("data-product-scan-exact=\"true\"", addPage);
        var stage = new Dictionary<string, string>
        {
            ["Batch.OperationId"] = Input(addPage, "Batch.OperationId"),
            ["Batch.WeekId"] = Input(addPage, "Batch.WeekId"),
            ["Batch.ExpectedWeekVersion"] = Input(addPage, "Batch.ExpectedWeekVersion"),
            ["Line.OperationId"] = Input(addPage, "Line.OperationId"),
            ["Line.WeekId"] = Input(addPage, "Line.WeekId"),
            ["Line.ExpectedWeekVersion"] = Input(addPage, "Line.ExpectedWeekVersion"),
            ["Line.ProductId"] = productId.ToString(),
            ["Line.ProductLabel"] = "GROUP-WEB",
            ["Line.PlannedDate"] = monday.AddDays(7).ToString("yyyy-MM-dd"),
            ["Line.Quantity"] = "7",
            ["__RequestVerificationToken"] = Input(addPage, "__RequestVerificationToken")
        };
        var stagedResponse = await client.PostAsync("/Admin/Production/Schedule?handler=StageLine",
            new FormUrlEncodedContent(stage));
        Assert.Equal(HttpStatusCode.OK, stagedResponse.StatusCode);
        var stagedPage = WebUtility.HtmlDecode(await stagedResponse.Content.ReadAsStringAsync());
        Assert.Contains("Pendiente de confirmar", stagedPage);
        Assert.Contains("bg-warning-subtle", stagedPage);
        Assert.False(await context.ProductionScheduleLines.AnyAsync(x => x.WeekId == targetId && !x.IsCancelled));
        var editStaged = new Dictionary<string, string>
        {
            ["Batch.OperationId"] = Input(stagedPage, "Batch.OperationId"),
            ["Batch.WeekId"] = Input(stagedPage, "Batch.WeekId"),
            ["Batch.ExpectedWeekVersion"] = Input(stagedPage, "Batch.ExpectedWeekVersion"),
            ["Batch.Rows[0].PlannedDate"] = Input(stagedPage, "Batch.Rows[0].PlannedDate"),
            ["Batch.Rows[0].ProductId"] = Input(stagedPage, "Batch.Rows[0].ProductId"),
            ["Batch.Rows[0].Quantity"] = Input(stagedPage, "Batch.Rows[0].Quantity"),
            ["index"] = "0",
            ["__RequestVerificationToken"] = Input(stagedPage, "__RequestVerificationToken")
        };
        var editStagedResponse = await client.PostAsync("/Admin/Production/Schedule?handler=EditStagedLine",
            new FormUrlEncodedContent(editStaged));
        Assert.Equal(HttpStatusCode.OK, editStagedResponse.StatusCode);
        var editingStagedPage = await editStagedResponse.Content.ReadAsStringAsync();
        Assert.Equal(7m, decimal.Parse(Input(editingStagedPage, "Line.Quantity"),
            System.Globalization.CultureInfo.InvariantCulture));
        stage["Batch.OperationId"] = Input(editingStagedPage, "Batch.OperationId");
        stage["Batch.WeekId"] = Input(editingStagedPage, "Batch.WeekId");
        stage["Batch.ExpectedWeekVersion"] = Input(editingStagedPage, "Batch.ExpectedWeekVersion");
        stage["Line.OperationId"] = Input(editingStagedPage, "Line.OperationId");
        stage["Line.WeekId"] = Input(editingStagedPage, "Line.WeekId");
        stage["Line.ExpectedWeekVersion"] = Input(editingStagedPage, "Line.ExpectedWeekVersion");
        stage["__RequestVerificationToken"] = Input(editingStagedPage, "__RequestVerificationToken");
        var restagedResponse = await client.PostAsync("/Admin/Production/Schedule?handler=StageLine",
            new FormUrlEncodedContent(stage));
        Assert.Equal(HttpStatusCode.OK, restagedResponse.StatusCode);
        stagedPage = WebUtility.HtmlDecode(await restagedResponse.Content.ReadAsStringAsync());
        Assert.Contains("Pendiente de confirmar", stagedPage);
        var secondStage = new Dictionary<string, string>
        {
            ["Batch.OperationId"] = Input(stagedPage, "Batch.OperationId"),
            ["Batch.WeekId"] = Input(stagedPage, "Batch.WeekId"),
            ["Batch.ExpectedWeekVersion"] = Input(stagedPage, "Batch.ExpectedWeekVersion"),
            ["Batch.Rows[0].PlannedDate"] = Input(stagedPage, "Batch.Rows[0].PlannedDate"),
            ["Batch.Rows[0].ProductId"] = Input(stagedPage, "Batch.Rows[0].ProductId"),
            ["Batch.Rows[0].Quantity"] = Input(stagedPage, "Batch.Rows[0].Quantity"),
            ["Line.OperationId"] = Input(stagedPage, "Line.OperationId"),
            ["Line.WeekId"] = Input(stagedPage, "Line.WeekId"),
            ["Line.ExpectedWeekVersion"] = Input(stagedPage, "Line.ExpectedWeekVersion"),
            ["Line.ProductId"] = productId.ToString(),
            ["Line.ProductLabel"] = "GROUP-WEB",
            ["Line.PlannedDate"] = monday.AddDays(8).ToString("yyyy-MM-dd"),
            ["Line.Quantity"] = "8",
            ["__RequestVerificationToken"] = Input(stagedPage, "__RequestVerificationToken")
        };
        var secondStagedResponse = await client.PostAsync("/Admin/Production/Schedule?handler=StageLine",
            new FormUrlEncodedContent(secondStage));
        Assert.Equal(HttpStatusCode.OK, secondStagedResponse.StatusCode);
        stagedPage = WebUtility.HtmlDecode(await secondStagedResponse.Content.ReadAsStringAsync());
        Assert.Contains("Pendientes de confirmar · 2", stagedPage);
        Assert.False(await context.ProductionScheduleLines.AnyAsync(x => x.WeekId == targetId && !x.IsCancelled));
        var confirmRows = new Dictionary<string, string>
        {
            ["Batch.OperationId"] = Input(stagedPage, "Batch.OperationId"),
            ["Batch.WeekId"] = Input(stagedPage, "Batch.WeekId"),
            ["Batch.ExpectedWeekVersion"] = Input(stagedPage, "Batch.ExpectedWeekVersion"),
            ["Batch.Rows[0].PlannedDate"] = Input(stagedPage, "Batch.Rows[0].PlannedDate"),
            ["Batch.Rows[0].ProductId"] = Input(stagedPage, "Batch.Rows[0].ProductId"),
            ["Batch.Rows[0].Quantity"] = Input(stagedPage, "Batch.Rows[0].Quantity"),
            ["Batch.Rows[1].PlannedDate"] = Input(stagedPage, "Batch.Rows[1].PlannedDate"),
            ["Batch.Rows[1].ProductId"] = Input(stagedPage, "Batch.Rows[1].ProductId"),
            ["Batch.Rows[1].Quantity"] = Input(stagedPage, "Batch.Rows[1].Quantity"),
            ["__RequestVerificationToken"] = Input(stagedPage, "__RequestVerificationToken")
        };
        var invalidGroup = new Dictionary<string, string>(confirmRows)
        {
            ["Batch.Rows[0].Quantity"] = "0"
        };
        var invalidGroupResponse = await client.PostAsync("/Admin/Production/Schedule?handler=SaveBatch",
            new FormUrlEncodedContent(invalidGroup));
        Assert.Equal(HttpStatusCode.OK, invalidGroupResponse.StatusCode);
        var invalidGroupPage = WebUtility.HtmlDecode(await invalidGroupResponse.Content.ReadAsStringAsync());
        Assert.Contains("Pendiente de confirmar", invalidGroupPage);
        Assert.False(await context.ProductionScheduleLines.AnyAsync(x => x.WeekId == targetId && !x.IsCancelled));
        confirmRows["__RequestVerificationToken"] = Input(invalidGroupPage, "__RequestVerificationToken");
        var confirmedResponse = await client.PostAsync("/Admin/Production/Schedule?handler=SaveBatch",
            new FormUrlEncodedContent(confirmRows));
        Assert.Equal(HttpStatusCode.Redirect, confirmedResponse.StatusCode);
        Assert.Equal([7m, 8m], (await context.ProductionScheduleLines.AsNoTracking()
            .Where(x => x.WeekId == targetId && !x.IsCancelled)
            .OrderBy(x => x.PlannedDate).Select(x => x.Quantity).ToArrayAsync()));

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
