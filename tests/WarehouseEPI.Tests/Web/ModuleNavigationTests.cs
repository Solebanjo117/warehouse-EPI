using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Web.Navigation;
using HubModel = WarehouseEPI.Web.Pages.Modules.IndexModel;

namespace WarehouseEPI.Tests.Web;

internal static class ModuleNavigationTestSupport
{
    public static ModuleAction[] Actions(bool admin = true) => ModuleNavigation.GetVisible(admin)
        .SelectMany(module => module.Sections).SelectMany(section => section.Actions).ToArray();
}

public sealed class ModuleNavigationTests
{
    [Fact]
    public void Modules_have_stable_order_and_public_navigation_never_exposes_admin_actions()
    {
        Assert.Equal(new[] { "operations", "production", "inventory", "labels", "reports", "catalogs", "administration" },
            ModuleNavigation.GetVisible(true).Select(module => module.Key));
        Assert.Equal(5, ModuleNavigation.GetVisible(false).Count);
        Assert.All(ModuleNavigationTestSupport.Actions(false), action =>
        {
            Assert.False(action.AdminOnly);
            Assert.False(action.Page.StartsWith("/Admin/", StringComparison.Ordinal));
        });
        Assert.All(ModuleNavigation.GetVisible(false), module =>
            Assert.All(module.Sections, section => Assert.NotEmpty(section.Actions)));
    }

    [Fact]
    public void All_existing_destinations_are_preserved_once_with_role_specific_parameters()
    {
        string[] expected = [
            "/Operations/Entry", "/Operations/Exit", "/Operations/Transfer", "/Operations/Adjustment",
            "/Operations/CycleCounts/Index", "/Operations/ProductionSupply/Index", "/Operations/Production/Index",
            "/Operations/Production/Index", "/Operations/Production/Advanced",
            "/Admin/Production/Processes",
            "/Admin/Production/Routes", "/Admin/Production/Schedule",
            "/Inventory/Index", "/Admin/Catalogs/Locations/Index", "/Admin/Inventory/Movements/Index",
            "/Admin/Inventory/Lots/Index", "/Admin/Inventory/Alerts", "/Operations/Labels/Index",
            "/Operations/PalletLabels/Index", "/Admin/Labels/Templates/Index", "/Reports/Dashboard/Index",
            "/Reports/Workload/Index", "/Reports/Inventory/Index", "/Reports/Kardex/Index", "/Reports/Wip/Index",
            "/Reports/Production/Index",
            "/Admin/Catalogs/Products/Index", "/Admin/Catalogs/ProductTypes/Index", "/Admin/Catalogs/ProductClasses/Index",
            "/Admin/Catalogs/Units/Index", "/Admin/Users/Index", "/Admin/System/Index", "/Admin/Settings/Business"];
        var actions = ModuleNavigationTestSupport.Actions();
        Assert.Equal(expected.Order(), actions.Select(action => action.Page).Order());
        Assert.Equal("pending", Assert.Single(actions, action => action.Page == "/Reports/Workload/Index").RouteValues["view"]);
        Assert.Empty(Assert.Single(ModuleNavigationTestSupport.Actions(false), action => action.Page == "/Reports/Workload/Index").RouteValues);
        Assert.Contains(ModuleNavigationTestSupport.Actions(false), action => action.Page == "/Locations/Index");
        Assert.Single(actions, action => action.SupplyCount);
        Assert.DoesNotContain(actions, action => action.Page == "/Admin/Production/Orders");
        Assert.DoesNotContain(actions, action => action.Page.Contains("/Receiving/", StringComparison.Ordinal) ||
            action.Page.Contains("/Trace/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/Admin/Catalogs/Products/Details", "catalogs")]
    [InlineData("/Admin/Catalogs/ProductTypes/Edit", "catalogs")]
    [InlineData("/Admin/Catalogs/Locations/Map/Edit", "inventory")]
    [InlineData("/Locations/Details", "inventory")]
    [InlineData("/Operations/ProductionSupply/Prepare", "production")]
    [InlineData("/Operations/Production/Execution", "production")]
    [InlineData("/Operations/WipReturn", "production")]
    [InlineData("/Admin/Production/ProcessEdit", "production")]
    [InlineData("/Admin/Labels/Assets/Index", "labels")]
    [InlineData("/Reports/Executive/Index", "reports")]
    [InlineData("/Reports/Workload/Index", "reports")]
    [InlineData("/Reports/Production/Index", "reports")]
    [InlineData("/Admin/Settings/Business", "administration")]
    [InlineData("/Operations/ProductionSupplyExtra/Index", null)]
    [InlineData("/Index", null)]
    public void Active_module_uses_full_path_segments(string page, string? expected)
        => Assert.Equal(expected, ModuleNavigation.Active(page, null, true)?.Key);

    [Theory]
    [InlineData("unknown", false, false, typeof(NotFoundResult))]
    [InlineData("catalogs", false, false, typeof(ChallengeResult))]
    [InlineData("administration", false, true, typeof(ForbidResult))]
    [InlineData("catalogs", true, true, typeof(PageResult))]
    [InlineData("production", false, false, typeof(PageResult))]
    public async Task Hub_enforces_access_before_exposing_actions(string module, bool admin, bool authenticated, Type resultType)
    {
        var identity = new ClaimsIdentity(admin ? [new Claim(ClaimTypes.Role, "ADMIN")] : [],
            authenticated ? "test" : null);
        var model = new HubModel(new RoleAuthorization())
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } }
        };
        Assert.IsType(resultType, await model.OnGetAsync(module));
        if (resultType == typeof(PageResult))
        {
            Assert.Equal(module, model.Module.Key);
            Assert.Equal(module, ModuleNavigation.Active("/Modules/Index", module, admin)?.Key);
        }
    }

    private sealed class RoleAuthorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        {
            Assert.Equal("AdminOnly", policyName);
            return Task.FromResult(user.IsInRole("ADMIN") ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) => throw new NotSupportedException();
    }
}
