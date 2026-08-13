using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

[Authorize(Policy = "AdminOnly")]
public sealed class CreateModel(WarehouseDbContext dbContext) : PageModel, IProductFormPage
{
    [BindProperty] public ProductInputModel Input { get; set; } = new();
    public IReadOnlyList<SelectListItem> Units { get; private set; }=[]; public IReadOnlyList<SelectListItem> Types { get; private set; }=[]; public IReadOnlyList<SelectListItem> Classes { get; private set; }=[];
    public async Task OnGetAsync(CancellationToken token){await LoadAsync(token);}
    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        ProductPageSupport.Normalize(Input); await ProductPageSupport.ValidateAsync(dbContext,Input,ModelState,token);
        if(!ModelState.IsValid){await LoadAsync(token);return Page();}
        var product=new Product{Sku=Input.Sku}; ProductPageSupport.Apply(product,Input); dbContext.Products.Add(product);
        try{await dbContext.SaveChangesAsync(token);}catch(DbUpdateException){ModelState.AddModelError("Input.Sku","No fue posible guardar; verifique que el SKU no esté repetido.");await LoadAsync(token);return Page();}
        return RedirectToPage("Edit",new{id=product.Id});
    }
    private async Task LoadAsync(CancellationToken token){(Units,Types,Classes)=await ProductPageSupport.LoadOptionsAsync(dbContext,Input,token);}
}
