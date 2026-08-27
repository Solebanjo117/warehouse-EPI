namespace WarehouseEPI.Tests.Web;

public sealed class RackOperationsContractTests
{
    [Fact]
    public void Croquis_exposes_prefilled_operations_and_reversible_rack_editor()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Index.cshtml"));
        Assert.Contains("Nueva operación", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-sourceLocationId", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-destinationLocationId", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-productId", page, StringComparison.Ordinal);
        Assert.Contains("Surtir a este WIP", page, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"Rack/Edit\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Rack_editor_keeps_review_reason_pin_and_physical_keypad_contracts()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Rack", "Edit.cshtml"));
        var model = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Rack", "Edit.cshtml.cs"));
        Assert.Contains("7, 8, 9, 4, 5, 6, 1, 2, 3", page, StringComparison.Ordinal);
        Assert.Contains("Revisar cambios", page, StringComparison.Ordinal);
        Assert.Contains("NIP ADMIN", page, StringComparison.Ordinal);
        Assert.Contains("No es eliminación física de datos", page, StringComparison.Ordinal);
        Assert.Contains("[Authorize(Policy = \"AdminOnly\")]", model, StringComparison.Ordinal);
        Assert.Contains("Input.Pin = string.Empty", model, StringComparison.Ordinal);
    }

    [Fact]
    public void Rack_editor_uses_two_column_workspace_with_sticky_review_panel()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Rack", "Edit.cshtml"));
        var css = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "css", "site.css"));

        // Workspace and column structure in the view
        Assert.Contains("rack-editor-workspace", page, StringComparison.Ordinal);
        Assert.Contains("rack-editor-main", page, StringComparison.Ordinal);
        Assert.Contains("rack-editor-review", page, StringComparison.Ordinal);

        // CSS grid layout for the workspace
        Assert.Contains(".rack-editor-workspace", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 1fr 22rem", css, StringComparison.Ordinal);

        // Sticky review panel at desktop
        Assert.Contains(".rack-editor-review", css, StringComparison.Ordinal);
        Assert.Contains("position: sticky", css, StringComparison.Ordinal);

        // Review panel is an aside with aria-labelledby
        Assert.Contains("aria-labelledby=\"rack-review-title\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Operation_get_validates_prefill_without_changing_post_contract()
    {
        var model = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Operations", "OperationPageModel.cs"));
        Assert.Contains("Task OnGetAsync(Guid? productId", model, StringComparison.Ordinal);
        Assert.Contains("LoadSelectionAsync(cancellationToken)", model, StringComparison.Ordinal);
        Assert.Contains("Input.ExpectedBalanceVersion = LocationBalance.Version", model, StringComparison.Ordinal);
        Assert.Contains("new InventoryMovementCommand(", model, StringComparison.Ordinal);
        Assert.Contains("public async Task<IActionResult> OnPostAsync", model, StringComparison.Ordinal);
    }

    private static string RepositoryPath(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "WarehouseEPI.sln")))
            current = current.Parent;
        return Path.Combine(current?.FullName ?? throw new InvalidOperationException("Repository root not found."), Path.Combine(segments));
    }
}
