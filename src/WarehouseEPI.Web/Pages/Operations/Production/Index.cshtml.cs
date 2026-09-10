using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;
namespace WarehouseEPI.Web.Pages.Operations.Production;
public sealed class IndexModel(ProductionQueryService query):PageModel
{
 [BindProperty(SupportsGet=true)]public string? Search{get;set;}[BindProperty(SupportsGet=true)]public ProductionWorkOrderStatus? Status{get;set;}public IReadOnlyList<ProductionOrderRow> Items{get;private set;}=[];
 public async Task OnGetAsync(CancellationToken t)=>Items=await query.GetOrdersAsync(Search,Status,100,t);
}
