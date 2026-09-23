using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Web.Production;
using Microsoft.AspNetCore.Mvc;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Operations.Production;

public sealed partial class IndexModel
{
    [BindProperty(SupportsGet = true)] public string Tab { get; set; } = "capture";
    [BindProperty] public GroupInput Group { get; set; } = new();
    public IReadOnlyList<ProductionAvailableProduct> Available { get; private set; } = [];
    public ProductionCaptureGroupPreview? GroupPreview { get; private set; }
    public ProductionScheduleWeekView? CaptureWeek { get; private set; }
    public string? CaptureContextMessage { get; private set; }
    public string? CaptureEmptyMessage { get; private set; }
    public Dictionary<ProductionDailyArea, int> OtherAvailableAreas { get; } = [];

    private async Task LoadGroupAsync(CancellationToken token)
    {
        if (Tab is not ("capture" or "balance" or "history")) Tab = "capture";
        if (Tab != "capture") return;
        var isGroupPost = HttpMethods.IsPost(Request.Method) && (RouteData.Values["handler"]?.ToString() ?? Request.Query["handler"].ToString()) is "GroupPreview" or "GroupConfirm" or "GroupAdd" or "GroupFilter";
        if (!isGroupPost)
        {
            var week = Weeks.SingleOrDefault(x => x.Id == WeekId);
            Group.Date = Day ?? (week is not null && (Today < week.WeekStart || Today > week.WeekEnd) ? week.WeekStart : Today);
            Group.Area = Area ?? ProductionDailyArea.Cutting;
            Group.ShiftId = ShiftId ?? Shifts.FirstOrDefault()?.Id ?? Guid.Empty;
        }
        CaptureWeek = Weeks.SingleOrDefault(x => x.WeekStart <= Group.Date && x.WeekEnd >= Group.Date);
        CaptureContextMessage = Group.Date > Today ? "La fecha efectiva no puede estar en el futuro."
            : Group.Date.DayOfWeek == DayOfWeek.Sunday ? "El domingo no admite captura ordinaria."
            : CaptureWeek is null ? "No hay una semana programada para esta fecha. Selecciona otra fecha o crea la semana en Programa semanal."
            : CaptureWeek.Status == ProductionScheduleWeekStatus.Draft ? "Esta semana está en borrador. Ábrela desde Programa semanal para empezar a capturar."
            : CaptureWeek.Status == ProductionScheduleWeekStatus.Closed ? "Esta semana está cerrada. Consulta su balance o solicita al administrador que la reabra."
            : null;
        if (ConfigurationReady && CaptureContextMessage is null)
            Available = await captures.GetAvailabilityAsync(Group.Date, Group.Area, token: token);
        if (!isGroupPost) Group.Rows = Available.Where(x => string.IsNullOrWhiteSpace(Sku) || x.Sku.Contains(Sku, StringComparison.OrdinalIgnoreCase) || x.Description.Contains(Sku, StringComparison.OrdinalIgnoreCase)).Take(100).Select(x => new GroupRowInput { ProductId = x.ProductId, Sku = x.Sku }).ToList();
        if (isGroupPost)
        {
            var rowIds = Group.Rows.Select(x => x.ProductId).Distinct().ToArray();
            var selected = await db.Products.AsNoTracking().Where(x => rowIds.Contains(x.Id))
                .Select(x => new ProductionAvailableProduct(x.Id, x.Sku, x.Description ?? "", 0, 0)).ToListAsync(token);
            Available = Available.Concat(selected.Where(x => !Available.Any(a => a.ProductId == x.ProductId))).ToArray();
            foreach (var row in Group.Rows) row.Sku = selected.FirstOrDefault(x => x.ProductId == row.ProductId)?.Sku;
        }
        if (ConfigurationReady && CaptureContextMessage is null && Group.Rows.Count == 0)
        {
            CaptureEmptyMessage = Available.Count > 0 ? "Ningún producto coincide con la búsqueda. Borra el texto y pulsa Buscar."
                : Group.Area == ProductionDailyArea.Cutting ? "No hay pendientes registrados. Agrega un producto para registrar la producción realizada."
                : "No hay pendientes registrados. Agrega un producto para registrar la producción realizada.";
            if (Available.Count == 0)
                foreach (var area in Enum.GetValues<ProductionDailyArea>().Where(x => x != Group.Area))
                {
                    var count = (await captures.GetAvailabilityAsync(Group.Date, area, token: token)).Count;
                    if (count > 0) OtherAvailableAreas[area] = count;
                }
        }
    }

    public Guid? FocusProductId { get; private set; }

    public async Task<IActionResult> OnGetDailyProductsAsync(DateOnly date, ProductionDailyArea area,
        string? q, int? category, int offset, CancellationToken token)
    {
        // This GET only validates lookup parameters, not the unrelated capture/PIN form.
        var parameters = new[] { "date", "area", "q", "category", "offset" };
        if (date == default || ModelState.Any(x => parameters.Contains(x.Key, StringComparer.OrdinalIgnoreCase) && x.Value?.Errors.Count > 0)
            || !(await ProductionDailySetup.LoadAsync(schedules, db, token)).IsReady)
            return new JsonResult(Array.Empty<ProductionProductSuggestionGroup>());
        return new JsonResult(await captures.SearchDailyProductsAsync(date, area, q, category, offset, token));
    }

    public async Task<IActionResult> OnPostGroupAddAsync(CancellationToken token)
    {
        ProductionCapture.ValidateOnly(this, nameof(Group));
        Group.Pin = ""; ProductionCapture.ClearPins(this);
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == Group.AddProductId && x.IsActive, token);
        if (product is null) ModelState.AddModelError(string.Empty, texts["Selecciona un SKU activo del catálogo."]);
        else if (!Group.Rows.Any(x => x.ProductId == product.Id))
        {
            if (Group.Rows.Count >= 100) Group.Rows = Group.Rows.Where(x => !string.IsNullOrWhiteSpace(x.Quantity) || !string.IsNullOrWhiteSpace(x.Notes)).ToList();
            if (Group.Rows.Count >= 100) ModelState.AddModelError(string.Empty, texts["Selecciona entre 1 y 100 productos sin repetir."]);
            else Group.Rows.Add(new GroupRowInput { ProductId = product.Id, Sku = product.Sku });
        }
        foreach (var key in ModelState.Keys.Where(x => x.StartsWith("Group.Rows[", StringComparison.Ordinal)).ToArray()) ModelState.Remove(key);
        FocusProductId = product?.Id;
        Group.Fingerprint = ""; ModelState.Remove("Group.Fingerprint");
        Tab = "capture";
        await LoadAsync(token, true);
        return Page();
    }

    public async Task<IActionResult> OnPostGroupFilterAsync(CancellationToken token)
    {
        Group.Pin = ""; ProductionCapture.ClearPins(this); Tab = "capture";
        await LoadAsync(token, true);
        var kept = Group.Rows.Where(x => !string.IsNullOrWhiteSpace(x.Quantity) || !string.IsNullOrWhiteSpace(x.Notes)).ToList();
        foreach (var product in Available.Where(x => string.IsNullOrWhiteSpace(Sku) || x.Sku.Contains(Sku, StringComparison.OrdinalIgnoreCase) || x.Description.Contains(Sku, StringComparison.OrdinalIgnoreCase)))
            if (kept.Count < 100 && !kept.Any(x => x.ProductId == product.ProductId)) kept.Add(new GroupRowInput { ProductId = product.ProductId, Sku = product.Sku });
        Group.Rows = kept;
        ModelState.Clear();
        return Page();
    }

    public Task<IActionResult> OnPostGroupPreviewAsync(CancellationToken token) => ProcessGroupAsync(false, token);
    public Task<IActionResult> OnPostGroupConfirmAsync(CancellationToken token) => ProcessGroupAsync(true, token);

    private async Task<IActionResult> ProcessGroupAsync(bool confirm, CancellationToken token)
    {
        Tab = "capture";
        ProductionCapture.ValidateOnly(this, nameof(Group));
        if (!await RequireConfigurationAsync(token))
        {
            Group.Pin = "";
            await LoadAsync(token, true);
            return Page();
        }
        var rows = new List<ProductionCaptureRow>();
        for (var index = 0; index < Group.Rows.Count; index++)
        {
            var row = Group.Rows[index];
            if (string.IsNullOrWhiteSpace(row.Quantity)) continue;
            if (!ProductionQuantityBinder.TryParse(row.Quantity, out var quantity))
                ModelState.AddModelError($"Group.Rows[{index}].Quantity", texts[ProductionQuantityBinder.Error]);
            else if (quantity != 0) rows.Add(new(row.ProductId, quantity, row.Notes));
        }
        var week = (await schedules.ListWeeksAsync(token)).SingleOrDefault(x => x.WeekStart <= Group.Date && x.WeekEnd >= Group.Date);
        WeekId = week?.Id;
        if (ModelState.IsValid)
        {
            var command = new ProductionCaptureGroupCommand(Group.OperationId, Group.Date, Group.Area, Group.ShiftId, rows, Group.Fingerprint ?? "", Group.Pin ?? "");
            if (confirm)
            {
                var result = await captures.ConfirmGroupAsync(command, token);
                if (result.Success)
                {
                    TempData["Success"] = texts["Tanda registrada; el balance y los pendientes se actualizaron."].Value;
                    return RedirectToPage(new { WeekId, Tab = "capture", Day = Group.Date.ToString("yyyy-MM-dd"), Area = Group.Area, ShiftId = Group.ShiftId });
                }
                foreach (var error in result.Errors ?? [result.Status == ProductionDailyCommandStatus.IdempotencyConflict
                    ? "La operación ya se utilizó con otros datos." : "No fue posible confirmar la tanda."])
                    ModelState.AddModelError(string.Empty, WarehouseEPI.Web.Production.ProductionDailyText.Message(texts, error));
            }
            GroupPreview = await captures.PreviewGroupAsync(command, token);
            Group.Fingerprint = GroupPreview.Fingerprint;
            ModelState.Remove("Group.Fingerprint");
        }
        Group.Pin = ""; ProductionCapture.ClearPins(this);
        await LoadAsync(token, true);
        return Page();
    }

    public sealed class GroupInput
    {
        public Guid? AddProductId { get; set; }
        public Guid OperationId { get; set; } = Guid.NewGuid();
        public DateOnly Date { get; set; }
        public ProductionDailyArea Area { get; set; }
        public Guid ShiftId { get; set; }
        public List<GroupRowInput> Rows { get; set; } = [];
        // Empty before the first review; it is checked by the service only when confirming.
        public string? Fingerprint { get; set; }
        public string? Pin { get; set; }
    }
    public sealed class GroupRowInput
    {
        public Guid ProductId { get; set; }
        public string? Sku { get; set; }
        public string? Quantity { get; set; }
        public string? Notes { get; set; }
    }
}
