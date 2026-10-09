using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Web.Security;

/// <summary>Explicit public surface. Unclassified Razor pages require administration.</summary>
public static class PageAccess
{
    public const string AdminOnly = "AdminOnly";
    public const string ProductsRead = "ProductsRead";
    public const string MovementsRead = "MovementsRead";
    public const string ScheduleRead = "ScheduleRead";
    public const string SignedIn = "SignedIn";

    private static readonly HashSet<string> PublicPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Index", "/Error", "/Privacy", "/Preferences/Language", "/BackupRestoreProgress",
        "/Admin/Login", "/Modules/Index", "/Inventory/Index", "/Reports/Notifications/Index",
        "/Operations/Entry", "/Operations/Exit", "/Operations/Transfer", "/Operations/Adjustment",
        "/Operations/Lookup", "/Operations/Receipt", "/Operations/WipIssue", "/Operations/WipProcess",
        "/Operations/WipReturn", "/Operations/WipReturnReceipt", "/Operations/Production/Index"
    };
    private static readonly string[] PublicFolders =
    ["/Locations", "/Operations/Receiving", "/Operations/CycleCounts", "/Operations/Labels",
        "/Operations/PalletLabels", "/Operations/ProductionSupply", "/Operations/Staging"];

    public static string? PolicyFor(string page)
    {
        if (PublicPages.Contains(page) || PublicFolders.Any(folder => page.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))) return null;
        if (page.Equals("/Admin/Logout", StringComparison.OrdinalIgnoreCase) || page.Equals("/AccessDenied", StringComparison.OrdinalIgnoreCase)) return SignedIn;
        if (page.Equals("/Admin/Catalogs/Products/Index", StringComparison.OrdinalIgnoreCase) || page.Equals("/Admin/Catalogs/Products/Details", StringComparison.OrdinalIgnoreCase)) return ProductsRead;
        if (page.Equals("/Admin/Inventory/Movements/Index", StringComparison.OrdinalIgnoreCase) || page.Equals("/Admin/Inventory/Movements/Details", StringComparison.OrdinalIgnoreCase) || page.Equals("/Admin/Reports/Movements/Index", StringComparison.OrdinalIgnoreCase)) return MovementsRead;
        if (page.Equals("/Production/Schedule", StringComparison.OrdinalIgnoreCase)) return ScheduleRead;
        return AdminOnly;
    }

    public static bool Allows(string? policy, string? role) => policy switch
    {
        null => true,
        ProductsRead or ScheduleRead => RoleAccess.CanCaptureProduction(role),
        MovementsRead => RoleAccess.CanOperateWarehouse(role),
        SignedIn => RoleAccess.IsKnown(role),
        _ => role == RoleAccess.Admin
    };

    public static void ConfigurePages(RazorPagesOptions options) =>
        options.Conventions.AddFolderApplicationModelConvention("/", model =>
        {
            var policy = PolicyFor(model.ViewEnginePath);
            if (policy is not null) model.Filters.Add(new AuthorizeFilter(policy));
        });

    public static void ConfigurePolicies(AuthorizationOptions options)
    {
        foreach (var name in new[] { AdminOnly, ProductsRead, MovementsRead, ScheduleRead, SignedIn })
        {
            var policy = name;
            options.AddPolicy(policy, builder => builder.RequireAuthenticatedUser()
                .RequireAssertion(context => Allows(policy, context.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value)));
        }
    }
}
