using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Admin.Production;

[Authorize(Policy="AdminOnly")]
public sealed class RoutesModel(WarehouseDbContext db,ProductionService service,UserPinService pins, IStringLocalizer<ProductionTexts> text):PageModel
{
    [BindProperty] public CatalogInput Catalog {get;set;}=new();
    [BindProperty] public RouteInput Route {get;set;}=new();
    public List<ProductionStage> Stages{get;private set;}=[]; public List<ProductionShift> Shifts{get;private set;}=[]; public List<Product> Products{get;private set;}=[]; public List<ProductionRoute> Routes{get;private set;}=[];
    public async Task OnGetAsync(Guid? productId,CancellationToken t){await LoadAsync(t);if(productId.HasValue&&Products.Any(x=>x.Id==productId.Value))Route.ProductId=productId.Value;}
    public async Task<IActionResult> OnPostCatalogAsync(CancellationToken t){ModelState.Clear();TryValidateModel(Catalog,nameof(Catalog));var u=await pins.AuthenticateAsync(Catalog.Pin,t);Catalog.Pin="";if(u?.Role.Code!="ADMIN")ModelState.AddModelError(string.Empty,text["NIP ADMIN inválido."].Value);if(!ModelState.IsValid){await LoadAsync(t);return Page();}var code=Catalog.Code.Trim().ToUpperInvariant();if(await db.ProductionShifts.AnyAsync(x=>x.Code==code,t)){ModelState.AddModelError(string.Empty,text["El código ya existe."].Value);await LoadAsync(t);return Page();}await new ProductionQueryService(db).AddCatalogItemAsync("shift",code,Catalog.Name,t);TempData["Success"]=text["Turno agregado."].Value;return RedirectToPage();}
    public async Task<IActionResult> OnPostRouteAsync(CancellationToken t){var r=await service.CreateRouteAsync(new(Route.OperationId,Route.ProductId,Route.Name,Route.StageIds,Route.Pin),t);Route.Pin="";if(r.Status==ProductionCommandStatus.Success){TempData["Success"]=text["Ruta creada y asignada al producto."].Value;return RedirectToPage();}ModelState.AddModelError(string.Empty,r.ValidationErrors.FirstOrDefault()??text["NIP ADMIN inválido o ruta no válida."].Value);await LoadAsync(t);return Page();}
    private async Task LoadAsync(CancellationToken t){Stages=await db.ProductionStages.AsNoTracking().OrderBy(x=>x.Name).ToListAsync(t);Shifts=await db.ProductionShifts.AsNoTracking().OrderBy(x=>x.Name).ToListAsync(t);Products=await db.Products.AsNoTracking().Where(x=>x.IsActive&&!db.ProductionRoutes.Any(r=>r.ProductId==x.Id&&r.IsActive)).OrderBy(x=>x.Sku).Take(2000).ToListAsync(t);Routes=await db.ProductionRoutes.AsNoTracking().Include(x=>x.Product).Include(x=>x.Stages).ThenInclude(x=>x.Stage).Where(x=>x.IsActive).OrderBy(x=>x.Product.Sku).ToListAsync(t);}
    public sealed class CatalogInput{[Required,StringLength(40)]public string Code{get;set;}="";[Required,StringLength(120)]public string Name{get;set;}="";[Required,RegularExpression("^[0-9]{4,8}$")]public string Pin{get;set;}="";}
    public sealed class RouteInput{public Guid OperationId{get;set;}=Guid.NewGuid();[Required]public Guid ProductId{get;set;}[Required,StringLength(120)]public string Name{get;set;}="";public List<Guid> StageIds{get;set;}=[];[Required,RegularExpression("^[0-9]{4,8}$")]public string Pin{get;set;}="";}
}
