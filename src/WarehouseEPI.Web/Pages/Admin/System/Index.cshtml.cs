using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Web.Observability;

namespace WarehouseEPI.Web.Pages.Admin.System;

public sealed class IndexModel(SystemStatusService systemStatus) : PageModel
{
    public SystemStatusSnapshot Status { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Status = await systemStatus.GetAsync(cancellationToken);
}
