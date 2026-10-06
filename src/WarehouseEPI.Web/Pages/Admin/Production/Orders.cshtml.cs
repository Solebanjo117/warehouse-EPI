using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Admin.Production;

[Authorize(Policy = "AdminOnly")]
public sealed class OrdersModel(ProductionQueryService query, ProductionService service, IStringLocalizer<ProductionTexts> text) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty] public InputModel Input { get; set; } = new(); public IReadOnlyList<ProductionOrderRow> Items { get; private set; } = []; public List<Product> Products { get; private set; } = [];
    public async Task OnGetAsync(CancellationToken t) => await LoadAsync(t);
    public async Task<IActionResult> OnPostAsync(CancellationToken t) { if (ModelState.IsValid) { var r = await service.CreateOrderAsync(new(Input.OperationId, Input.ProductId, Input.TargetQuantity, Input.ExternalReference, Input.DueDate, Input.Notes, Input.Pin), t); Input.Pin = ""; if (r.Status == ProductionCommandStatus.Success) return RedirectToPage("Details", new { id = r.WorkOrderId }); ModelState.AddModelError(string.Empty, r.ValidationErrors.FirstOrDefault() ?? text["NIP ADMIN inválido o datos no válidos."].Value); } Input.Pin = ""; await LoadAsync(t); return Page(); }
    private async Task LoadAsync(CancellationToken t) { Items = await query.GetOrdersAsync(Search, null, 100, t); Products = await query.GetRoutedProductsAsync(t); }
    public sealed class InputModel { public Guid OperationId { get; set; } = Guid.NewGuid(); [Required] public Guid ProductId { get; set; } [Range(typeof(decimal), "0.0001", "99999999999999")] public decimal TargetQuantity { get; set; } [StringLength(120)] public string? ExternalReference { get; set; } public DateOnly? DueDate { get; set; } [StringLength(500)] public string? Notes { get; set; } [Required, RegularExpression("^[0-9]{4,8}$")] public string Pin { get; set; } = ""; }
}
