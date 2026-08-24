using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Reporting;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public sealed class ExportModel(CycleCountService cycleCountService, ReportExportService exportService) : PageModel
{
    public async Task<IActionResult> OnGetAsync(Guid id, string format, CancellationToken cancellationToken)
    {
        if (format is not ("csv" or "xlsx")) return BadRequest("El formato debe ser csv o xlsx.");
        var rows = await cycleCountService.GetExportRowsAsync(id, 10000, cancellationToken);
        if (rows.Count > 10000) return BadRequest("La campaña supera el límite de 10,000 líneas; no se generó un archivo parcial.");
        var bytes = format == "xlsx" ? await exportService.ExportCycleCountsToExcelAsync(rows, cancellationToken) : await exportService.ExportCycleCountsToCsvAsync(rows, cancellationToken);
        return File(bytes, format == "xlsx" ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/csv; charset=utf-8", $"conteo-ciclico-{id:N}.{format}");
    }
}
