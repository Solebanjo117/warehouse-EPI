using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Web.Pages.Operations.Production;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionCaptureTests
{
    [Theory]
    [InlineData("0.25", true)]
    [InlineData("0", true)]
    [InlineData("12.1234", true)]
    [InlineData("0,25", false)]
    [InlineData("1,500", false)]
    [InlineData("1.000,25", false)]
    [InlineData("-1", false)]
    [InlineData("1e3", false)]
    [InlineData("1.12345", false)]
    [InlineData("", false)]
    public void Quantity_format_is_explicit(string input, bool expected) =>
        Assert.Equal(expected, ProductionQuantityBinder.TryParse(input, out _));

    [Fact]
    public void Scoped_validation_preserves_capture_and_clears_only_other_forms_and_secrets()
    {
        var page = new CapturePage();
        page.ModelState.SetModelValue("BatchResult.InputQuantity", "0,25", "0,25");
        page.ModelState.AddModelError("BatchResult.InputQuantity", ProductionQuantityBinder.Error);
        page.ModelState.AddModelError("Material.Pin", "Required");
        page.ModelState.SetModelValue("BatchResult.Pin", "1234", "1234");
        Assert.False(ProductionCapture.ValidateOnly(page, "BatchResult"));
        ProductionCapture.ClearPins(page);
        Assert.False(page.ModelState.ContainsKey("Material.Pin"));
        Assert.False(page.ModelState.ContainsKey("BatchResult.Pin"));
        Assert.Equal("0,25", ProductionCapture.Value(page, "BatchResult.InputQuantity", 0));
    }

    private sealed class CapturePage : PageModel { }
}
