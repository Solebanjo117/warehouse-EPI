using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

[Authorize(Policy = "AdminOnly")]
public sealed class EditModel(
    WarehouseDbContext dbContext,
    ProductLocationAssignmentService assignmentService) : PageModel, IProductFormPage
{
    private static readonly string[] AllowedFormats = ["CODE_128", "EAN_13", "EAN_8", "UPC_A", "UPC_E", "QR", "OTHER"];
    [BindProperty] public ProductInputModel Input { get; set; } = new();
    public BarcodeInputModel BarcodeInput { get; set; } = new();
    public IReadOnlyList<SelectListItem> Units { get; private set; }=[]; public IReadOnlyList<SelectListItem> Types { get; private set; }=[]; public IReadOnlyList<SelectListItem> Classes { get; private set; }=[];
    public IReadOnlyList<SelectListItem> BarcodeFormats { get; } = AllowedFormats.Select(x => new SelectListItem(x, x)).ToList();
    public IReadOnlyList<BarcodeRow> Barcodes { get; private set; }=[];
    public IReadOnlyList<LocationAssignmentRow> LocationAssignments { get; private set; }=[];
    public IReadOnlyList<LocationSearchRow> LocationResults { get; private set; }=[];
    public string? LocationSearch { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id,string? locationSearch,CancellationToken token)
    {
        LocationSearch=locationSearch?.Trim();
        var product=await dbContext.Products.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,token); if(product is null)return NotFound();
        Input=new ProductInputModel{Id=product.Id,Sku=product.Sku,Description=product.Description,ExternalReference=product.ExternalReference,ProductTypeId=product.ProductTypeId,ProductClassId=product.ProductClassId,BaseUnitId=product.BaseUnitId,MinimumStock=product.MinimumStock,TracksLots=product.TracksLots,TracksExpiration=product.TracksExpiration,IsActive=product.IsActive};
        await LoadAsync(token);return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        ProductPageSupport.Normalize(Input);await ProductPageSupport.ValidateAsync(dbContext,Input,ModelState,token);
        var product=await dbContext.Products.SingleOrDefaultAsync(x=>x.Id==Input.Id,token);if(product is null)return NotFound();
        if(!ModelState.IsValid){await LoadAsync(token);return Page();}
        ProductPageSupport.Apply(product,Input);
        try{await dbContext.SaveChangesAsync(token);}catch(DbUpdateException){ModelState.AddModelError("Input.Sku","No fue posible guardar; verifique que el SKU no esté repetido.");await LoadAsync(token);return Page();}
        TempData["Success"]="Producto actualizado.";return RedirectToPage(new{id=Input.Id});
    }

    public async Task<IActionResult> OnPostAddBarcodeAsync(Guid id,[Bind(Prefix="BarcodeInput")]BarcodeInputModel barcodeInput,CancellationToken token)
    {
        var product=await dbContext.Products.AnyAsync(x=>x.Id==id,token);if(!product)return NotFound();
        var code=barcodeInput.Barcode.Trim();var format=barcodeInput.Format.Trim().ToUpperInvariant();
        if(string.IsNullOrEmpty(code)||code.Length>100||!AllowedFormats.Contains(format)){TempData["Error"]="Capture un código y formato válidos.";return RedirectToPage(new{id});}
        var existing=await dbContext.ProductBarcodes.SingleOrDefaultAsync(x=>x.Barcode==code,token);
        if(existing is not null&&existing.ProductId!=id){TempData["Error"]="El código ya pertenece a otro producto.";return RedirectToPage(new{id});}
        if(existing is not null&&existing.IsActive){TempData["Error"]="El producto ya tiene ese código activo.";return RedirectToPage(new{id});}
        if(barcodeInput.IsPrimary)await ClearPrimaryAsync(id,token);
        if(existing is null)dbContext.ProductBarcodes.Add(new ProductBarcode{ProductId=id,Barcode=code,Format=format,IsPrimary=barcodeInput.IsPrimary});
        else{existing.IsActive=true;existing.Format=format;existing.IsPrimary=barcodeInput.IsPrimary;}
        await dbContext.SaveChangesAsync(token);return RedirectToPage(new{id});
    }

    public async Task<IActionResult> OnPostAssignLocationAsync(Guid id,Guid locationId,CancellationToken token)
    {
        var result=await assignmentService.AssignAsync(id,locationId,token);
        if(result==ProductLocationAssignmentResult.Success)TempData["Success"]="Ubicación asignada al producto.";
        else TempData["Error"]=AssignmentError(result);
        return RedirectToPage(new{id});
    }

    public async Task<IActionResult> OnPostDeactivateLocationAsync(Guid id,Guid locationId,CancellationToken token)
    {
        var result=await assignmentService.DeactivateAsync(id,locationId,token);
        if(result==ProductLocationAssignmentResult.Success)TempData["Success"]="La asignación fue desactivada.";
        else TempData["Error"]="La asignación activa ya no existe.";
        return RedirectToPage(new{id});
    }

    public async Task<IActionResult> OnPostToggleBarcodeAsync(Guid id,Guid barcodeId,CancellationToken token)
    {
        var barcode=await dbContext.ProductBarcodes.SingleOrDefaultAsync(x=>x.Id==barcodeId&&x.ProductId==id,token);if(barcode is null)return NotFound();
        barcode.IsActive=!barcode.IsActive;if(!barcode.IsActive)barcode.IsPrimary=false;await dbContext.SaveChangesAsync(token);return RedirectToPage(new{id});
    }

    public async Task<IActionResult> OnPostSetPrimaryAsync(Guid id,Guid barcodeId,CancellationToken token)
    {
        var barcode=await dbContext.ProductBarcodes.SingleOrDefaultAsync(x=>x.Id==barcodeId&&x.ProductId==id&&x.IsActive,token);if(barcode is null)return NotFound();
        await ClearPrimaryAsync(id,token);barcode.IsPrimary=true;await dbContext.SaveChangesAsync(token);return RedirectToPage(new{id});
    }

    private async Task ClearPrimaryAsync(Guid productId,CancellationToken token)
    {
        var current=await dbContext.ProductBarcodes.Where(x=>x.ProductId==productId&&x.IsPrimary).ToListAsync(token);foreach(var item in current)item.IsPrimary=false;
    }
    private async Task LoadAsync(CancellationToken token)
    {
        (Units,Types,Classes)=await ProductPageSupport.LoadOptionsAsync(dbContext,Input,token);
        Barcodes=await dbContext.ProductBarcodes.AsNoTracking().Where(x=>x.ProductId==Input.Id).OrderByDescending(x=>x.IsPrimary).ThenBy(x=>x.Barcode).Select(x=>new BarcodeRow(x.Id,x.Barcode,x.Format,x.IsPrimary,x.IsActive)).ToListAsync(token);
        LocationAssignments=await dbContext.ProductLocationAssignments.AsNoTracking().Where(x=>x.ProductId==Input.Id)
            .OrderByDescending(x=>x.IsActive).ThenBy(x=>x.Location.RowCode).ThenBy(x=>x.Location.RackNumber).ThenBy(x=>x.Location.PalletNumber).ThenBy(x=>x.Location.Code)
            .Select(x=>new LocationAssignmentRow(x.LocationId,x.Location.Code,x.Location.Description,x.Location.IsActive,x.Location.IsBlocked,x.IsActive)).ToListAsync(token);
        if(!string.IsNullOrWhiteSpace(LocationSearch))
        {
            var term=LocationSearch.ToUpperInvariant();
            LocationResults=await dbContext.Locations.AsNoTracking().Where(x=>x.IsActive&&!x.IsBlocked&&
                    (x.Code.Contains(term)||(x.Description!=null&&x.Description.ToUpper().Contains(term))))
                .OrderBy(x=>x.RowCode).ThenBy(x=>x.RackNumber).ThenBy(x=>x.PalletNumber).ThenBy(x=>x.Code).Take(20)
                .Select(x=>new LocationSearchRow(x.Id,x.Code,x.Description,
                    x.ProductAssignments.Any(a=>a.ProductId==Input.Id&&a.IsActive))).ToListAsync(token);
        }
    }
    private static string AssignmentError(ProductLocationAssignmentResult result)=>result switch
    {
        ProductLocationAssignmentResult.AlreadyActive=>"El producto ya está asignado a esa ubicación.",
        ProductLocationAssignmentResult.ProductInactive=>"No se puede asignar un producto inactivo.",
        ProductLocationAssignmentResult.LocationInactive=>"No se puede asignar a una ubicación inactiva.",
        ProductLocationAssignmentResult.LocationBlocked=>"No se puede asignar a una ubicación bloqueada.",
        _=>"El producto o la ubicación ya no existe."
    };
    public sealed class BarcodeInputModel{[Required,StringLength(100)]public string Barcode{get;set;}=string.Empty;[Required]public string Format{get;set;}="CODE_128";public bool IsPrimary{get;set;}}
    public sealed record BarcodeRow(Guid Id,string Barcode,string Format,bool IsPrimary,bool IsActive);
    public sealed record LocationAssignmentRow(Guid LocationId,string Code,string? Description,bool LocationIsActive,bool LocationIsBlocked,bool IsActive);
    public sealed record LocationSearchRow(Guid Id,string Code,string? Description,bool IsAssigned);
}
