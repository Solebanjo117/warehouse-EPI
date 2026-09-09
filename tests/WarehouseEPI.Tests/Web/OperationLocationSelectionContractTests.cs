namespace WarehouseEPI.Tests.Web;

public sealed class OperationLocationSelectionContractTests
{
    [Fact]
    public void Product_location_autoselection_uses_only_an_available_explicit_entry_default()
    {
        var script = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "wwwroot", "js", "operations.js"));

        Assert.Contains(
            "operation === \"entry\" && !selected[primaryLocationKind] && selected.product.defaultEntryLocationId",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "selected.product.isDefaultEntryLocationAvailable && defaultLocation",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Destino principal aplicado: ${defaultLocation.code}. Puedes cambiarlo.",
            script,
            StringComparison.Ordinal);
        Assert.Contains("La ubicación principal ${code} no está disponible.", script, StringComparison.Ordinal);
        Assert.Contains(
            "new URLSearchParams({ handler: \"ProductLocations\", productId, operation })",
            script,
            StringComparison.Ordinal);
        Assert.Contains("Sin ubicaciones con saldo. Escanea o escribe una ubicación.", script, StringComparison.Ordinal);
        Assert.Contains(
            "Sin productos con saldo. Busca o escanea un producto; se asociará al confirmar.",
            script,
            StringComparison.Ordinal);
        Assert.Contains("operation !== \"entry\" && !selected[primaryLocationKind] && items.length === 1", script, StringComparison.Ordinal);
        Assert.Contains(
            "operation !== \"entry\" && canSelectProductFrom(kind) && !selected.product && items.length === 1",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (canSelectProductFrom(kind) && !selected.product && items.length === 1)",
            script,
            StringComparison.Ordinal);
        Assert.Contains("(item) => void applySelection(primaryLocationKind, item, true)", script, StringComparison.Ordinal);
        Assert.Contains(
            "selectable ? (item) => void applySelection(\"product\", item, true)",
            script,
            StringComparison.Ordinal);
        Assert.Contains("autoSelectedEntryDestinationForProductId === selected.product?.id", script, StringComparison.Ordinal);
        Assert.Contains("clearSelection(primaryLocationKind)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Next_required_location_keeps_related_choices_but_focuses_the_scan_input()
    {
        var script = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "wwwroot", "js", "operations.js"));

        Assert.Contains("renderRelationshipChoices(", script, StringComparison.Ordinal);
        Assert.Contains("focusLookupInput(next);", script, StringComparison.Ordinal);
        Assert.Contains("focusLookupInput(missing);", script, StringComparison.Ordinal);
        Assert.Contains("input?.focus();", script, StringComparison.Ordinal);
        Assert.Contains("input?.select();", script, StringComparison.Ordinal);
        Assert.DoesNotContain("relationshipButton", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Hid_scanner_uses_shared_fast_capture_only_outside_editable_fields()
    {
        var capture = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "wwwroot", "js", "hid-capture.js"));
        var operations = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "wwwroot", "js", "operations.js"));
        var locations = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "wwwroot", "js", "location-index.js"));
        var locationPartial = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Locations", "_LocationIndex.cshtml"));

        Assert.Contains("maxGapMs = 100", capture, StringComparison.Ordinal);
        Assert.Contains("resetMs = 250", capture, StringComparison.Ordinal);
        Assert.Contains("minLength = 2", capture, StringComparison.Ordinal);
        Assert.Contains("document.addEventListener(\"keydown\", keydown, { capture: true });", capture, StringComparison.Ordinal);
        Assert.Contains("code.length >= minLength && now - lastKeyAt <= maxGapMs", capture, StringComparison.Ordinal);
        Assert.Contains("window.setTimeout(reset, resetMs)", capture, StringComparison.Ordinal);
        Assert.Contains("event.preventDefault();", capture, StringComparison.Ordinal);
        Assert.Contains("event.stopPropagation();", capture, StringComparison.Ordinal);
        Assert.Contains("document.querySelector(\".modal.show\")", capture, StringComparison.Ordinal);
        Assert.Contains("input, textarea, select, [contenteditable]", capture, StringComparison.Ordinal);
        Assert.Contains("target !== document.body && !root.contains(target)", capture, StringComparison.Ordinal);
        Assert.Contains("if (event.key === \"Shift\") return;", capture, StringComparison.Ordinal);
        Assert.Contains("if (event.key === \" \")", capture, StringComparison.Ordinal);
        Assert.Contains("window.WarehouseEpiHidCapture = { listen };", capture, StringComparison.Ordinal);

        Assert.Contains("window.WarehouseEpiHidCapture?.listen({", operations, StringComparison.Ordinal);
        Assert.Contains("root: operationShell", operations, StringComparison.Ordinal);
        Assert.Contains("const targetKind = nextRequiredKind();", operations, StringComparison.Ordinal);
        Assert.Contains("resolveLookupCode(targetKind, code, true)", operations, StringComparison.Ordinal);
        Assert.Contains("input.addEventListener(\"keydown\", async (event)", operations, StringComparison.Ordinal);

        Assert.Contains("data-location-search-form", locationPartial, StringComparison.Ordinal);
        Assert.Contains("data-location-search-input", locationPartial, StringComparison.Ordinal);
        Assert.Contains("root: document.body", locations, StringComparison.Ordinal);
        Assert.Contains("input.value = code;", locations, StringComparison.Ordinal);
        Assert.Contains("form.requestSubmit();", locations, StringComparison.Ordinal);

        foreach (var pageName in new[] { "Entry", "Exit", "Transfer", "Adjustment" })
        {
            var page = File.ReadAllText(RepositoryPath(
                "src", "WarehouseEPI.Web", "Pages", "Operations", $"{pageName}.cshtml"));
            Assert.Contains("js/hid-capture.js", page, StringComparison.Ordinal);
            Assert.Contains("js/operations.js", page, StringComparison.Ordinal);
            Assert.True(
                page.IndexOf("js/hid-capture.js", StringComparison.Ordinal) <
                page.IndexOf("js/operations.js", StringComparison.Ordinal));
        }

        foreach (var pagePath in new[]
        {
            new[] { "Locations", "Index.cshtml" },
            new[] { "Admin", "Catalogs", "Locations", "Index.cshtml" }
        })
        {
            var page = File.ReadAllText(RepositoryPath(
                ["src", "WarehouseEPI.Web", "Pages", .. pagePath]));
            Assert.Contains("js/hid-capture.js", page, StringComparison.Ordinal);
            Assert.Contains("js/location-index.js", page, StringComparison.Ordinal);
            Assert.True(
                page.IndexOf("js/hid-capture.js", StringComparison.Ordinal) <
                page.IndexOf("js/location-index.js", StringComparison.Ordinal));
            Assert.DoesNotContain("js/operations.js", page, StringComparison.Ordinal);
            Assert.DoesNotContain("zxing-browser.min.js", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Related_product_and_location_choices_advance_focus_to_the_next_required_step()
    {
        var script = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "wwwroot", "js", "operations.js"));

        Assert.Contains(
            "applySelection(primaryLocationKind, item, true).then(focusNextRequired)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "applySelection(\"product\", item, true).then(focusNextRequired)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (next === \"quantity\") { quantityInput.focus(); quantityInput.select(); return; }",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Enter_on_quantity_validates_advances_required_steps_and_opens_pin_confirmation()
    {
        var script = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "wwwroot", "js", "operations.js"));

        Assert.Contains("quantityInput.addEventListener(\"keydown\", (event)", script, StringComparison.Ordinal);
        Assert.Contains("event.key !== \"Enter\" || event.isComposing", script, StringComparison.Ordinal);
        Assert.Contains("event.preventDefault();", script, StringComparison.Ordinal);
        Assert.Contains("if (!quantityInput.checkValidity())", script, StringComparison.Ordinal);
        Assert.Contains("quantityInput.reportValidity();", script, StringComparison.Ordinal);
        Assert.Contains("const next = nextRequiredKind();", script, StringComparison.Ordinal);
        Assert.Contains("if (next) {", script, StringComparison.Ordinal);
        Assert.Contains("focusNextRequired();", script, StringComparison.Ordinal);
        Assert.Contains("kind === \"notes\" ? !notesInput?.value.trim()", script, StringComparison.Ordinal);
        Assert.Contains("openConfirmation();", script, StringComparison.Ordinal);
        Assert.Contains("reviewButton.addEventListener(\"click\", openConfirmation);", script, StringComparison.Ordinal);
        Assert.Contains("modal.show();", script, StringComparison.Ordinal);

        foreach (var pageName in new[] { "Entry", "Exit", "Transfer", "Adjustment" })
        {
            var page = File.ReadAllText(RepositoryPath(
                "src", "WarehouseEPI.Web", "Pages", "Operations", $"{pageName}.cshtml"));
            Assert.Contains("<partial name=\"_GuidedMovementForm\" model=\"Model\" />", page, StringComparison.Ordinal);
        }
    }

    private static string RepositoryPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
