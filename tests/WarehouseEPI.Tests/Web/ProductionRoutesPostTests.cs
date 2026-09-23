using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionRoutesPostTests
{
    [Fact]
    public async Task Shift_form_saves_without_route_fields_and_lists_the_new_shift()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var role = await db.Roles.SingleAsync(x => x.Code == "ADMIN");
            var user = new User { FullName = "Routes test admin", RoleId = role.Id, PinLookup = "", PinHash = "" };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
            db.Add(user);
            await db.SaveChangesAsync();
        }
        var login = await client.GetStringAsync("/Admin/Login");
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Admin/Login", login, new() { ["Input.Pin"] = "0123" })).StatusCode);

        var html = await client.GetStringAsync("/Admin/Production/Routes");
        var response = await Post(client, "/Admin/Production/Routes?handler=Catalog", html,
            new() { ["Catalog.Code"] = " t3 ", ["Catalog.Name"] = "Tercer turno", ["Catalog.Pin"] = "0123" });

        Assert.True(response.StatusCode == HttpStatusCode.Redirect,
            $"Alta de turno devolvió {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        using (var scope = factory.Services.CreateScope())
        {
            var shift = await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionShifts.SingleAsync(x => x.Code == "T3");
            Assert.Equal("Tercer turno", shift.Name);
        }
        var after = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Production/Routes"));
        Assert.Contains("T3 · Tercer turno", after, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string url, string html, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }
}
