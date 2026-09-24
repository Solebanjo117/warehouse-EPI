using WarehouseEPI.Web.Production;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Operations.Production;

public sealed partial class IndexModel
{
    [BindProperty(SupportsGet = true)] public string? Tab { get; set; } = "capture";
    [BindProperty] public GroupInput Group { get; set; } = new();
    public IReadOnlyList<ProductionAvailableProduct> Available { get; private set; } = [];
    public ProductionCaptureGroupPreview? GroupPreview { get; private set; }
    public ProductionScheduleWeekView? CaptureWeek { get; private set; }
    public string? CaptureContextMessage { get; private set; }
    public string? CaptureEmptyMessage { get; private set; }
    public Dictionary<ProductionDailyArea, int> OtherAvailableAreas { get; } = [];
    public IReadOnlyDictionary<Guid, decimal> RegisteredInShift { get; private set; } = new Dictionary<Guid, decimal>();
    public IReadOnlyDictionary<Guid, CaptureProductMeta> CaptureProducts { get; private set; } = new Dictionary<Guid, CaptureProductMeta>();
    [BindProperty(SupportsGet = true)] public Guid? ReceiptId { get; set; }
    public CaptureReceipt? Receipt { get; private set; }

    private async Task LoadGroupAsync(CancellationToken token)
    {
        if (Tab is not ("capture" or "balance" or "history")) Tab = "capture";
        if (Tab != "capture") return;
        var handler = RouteData.Values["handler"]?.ToString() ?? Request.Query["handler"].ToString();
        var isGroupPost = HttpMethods.IsPost(Request.Method) && handler is "GroupPreview" or "GroupConfirm" or "GroupAdd" or "GroupFilter" or "GroupMode";
        if (!isGroupPost)
        {
            var week = Weeks.SingleOrDefault(x => x.Id == WeekId);
            Group.Date = Day ?? (week is not null && (Today < week.WeekStart || Today > week.WeekEnd) ? week.WeekStart : Today);
            Group.Area = Area ?? ProductionDailyArea.Cutting;
            Group.ShiftId = ShiftId ?? Shifts.FirstOrDefault()?.Id ?? Guid.Empty;
        }
        Group.Mode = Group.Mode == "quick" ? "quick" : "list";
        CaptureWeek = Weeks.SingleOrDefault(x => x.WeekStart <= Group.Date && x.WeekEnd >= Group.Date);
        CaptureContextMessage = CaptureWeek is null ? "No hay una semana programada para esta fecha. Selecciona otra fecha o crea la semana en Programa semanal."
            : CaptureWeek.Status == ProductionScheduleWeekStatus.Draft ? "Esta semana está en borrador. Ábrela desde Programa semanal para empezar a capturar."
            : CaptureWeek.Status == ProductionScheduleWeekStatus.Closed ? "Esta semana está cerrada. Consulta su balance o solicita al administrador que la reabra."
            : null;
        if (ConfigurationReady && CaptureContextMessage is null)
            Available = await captures.GetAvailabilityAsync(Group.Date, Group.Area, token: token);
        if (!isGroupPost) Group.Rows = Available.Where(x => string.IsNullOrWhiteSpace(Sku) || x.Sku.Contains(Sku, StringComparison.OrdinalIgnoreCase) || x.Description.Contains(Sku, StringComparison.OrdinalIgnoreCase)).Take(100).Select(x => new GroupRowInput { ProductId = x.ProductId, Sku = x.Sku }).ToList();
        if (isGroupPost)
        {
            var selectedIds = Group.Rows.Select(x => x.ProductId).Distinct().ToArray();
            var selected = await db.Products.AsNoTracking().Where(x => selectedIds.Contains(x.Id))
                .Select(x => new ProductionAvailableProduct(x.Id, x.Sku, x.Description ?? "", 0, 0)).ToListAsync(token);
            Available = Available.Concat(selected.Where(x => !Available.Any(a => a.ProductId == x.ProductId))).ToArray();
            foreach (var row in Group.Rows) row.Sku = selected.FirstOrDefault(x => x.ProductId == row.ProductId)?.Sku;
        }
        if (handler == "GroupMode" && Group.Mode == "list")
        {
            var kept = Group.Rows.Where(x => !string.IsNullOrWhiteSpace(x.Quantity) || !string.IsNullOrWhiteSpace(x.Notes)).ToList();
            foreach (var product in Available.Where(x => string.IsNullOrWhiteSpace(Sku) ||
                x.Sku.Contains(Sku, StringComparison.OrdinalIgnoreCase) || x.Description.Contains(Sku, StringComparison.OrdinalIgnoreCase)))
                if (kept.Count < 100 && kept.All(x => x.ProductId != product.ProductId))
                    kept.Add(new GroupRowInput { ProductId = product.ProductId, Sku = product.Sku });
            Group.Rows = kept;
            ModelState.Clear();
        }
        var rowIds = Group.Rows.Select(x => x.ProductId).Distinct().ToArray();
        CaptureProducts = await db.Products.AsNoTracking().Where(x => rowIds.Contains(x.Id))
            .Select(x => new { x.Id, Unit = x.BaseUnit.Code })
            .ToDictionaryAsync(x => x.Id, x => new CaptureProductMeta(x.Unit), token);
        RegisteredInShift = await db.ProductionDailyCaptures.AsNoTracking()
            .Where(x => x.EffectiveDate == Group.Date && x.Area == Group.Area && x.ShiftId == Group.ShiftId
                && x.Status == ProductionDailyCaptureStatus.Active && rowIds.Contains(x.ProductId))
            .GroupBy(x => x.ProductId).Select(x => new { ProductId = x.Key, Quantity = x.Sum(c => c.Quantity) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Quantity, token);
        if (ReceiptId is Guid receiptId)
        {
            var submission = await db.ProductionCaptureSubmissions.AsNoTracking()
                .Include(x => x.Items).ThenInclude(x => x.Capture).ThenInclude(x => x.Product).ThenInclude(x => x.BaseUnit)
                .SingleOrDefaultAsync(x => x.Id == receiptId, token);
            if (submission is not null && submission.Items.Count > 0 && CaptureWeek is not null &&
                submission.Items.All(x => x.Capture.WeekId == CaptureWeek.Id &&
                    x.Capture.EffectiveDate == Group.Date && x.Capture.Area == Group.Area && x.Capture.ShiftId == Group.ShiftId))
            {
                var actor = await db.Users.AsNoTracking().Where(x => x.Id == submission.ResponsibleUserId)
                    .Select(x => x.FullName).SingleAsync(token);
                Receipt = new CaptureReceipt(submission.Id, submission.RecordedAt, actor,
                    submission.Items.OrderBy(x => x.Capture.Product.Sku).Select(x => new CaptureReceiptItem(
                        x.Capture.Id, x.Capture.EffectiveDate, x.Capture.Area, x.Capture.ShiftId,
                        x.Capture.Product.Sku, x.Capture.Quantity, x.Capture.Product.BaseUnit.Code)).ToArray());
            }
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

    public async Task<IActionResult> OnPostGroupModeAsync(CancellationToken token)
    {
        Group.Pin = ""; ProductionCapture.ClearPins(this);
        Group.Fingerprint = "";
        Tab = "capture";
        await LoadAsync(token, true);
        return Page();
    }

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
        if (Group.Mode == "quick") Group.Rows = Group.Rows.Where(x => !string.IsNullOrWhiteSpace(x.Quantity) || !string.IsNullOrWhiteSpace(x.Notes)).ToList();
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
                    return RedirectToPage(new { WeekId, Tab = "capture", Day = Group.Date.ToString("yyyy-MM-dd"), Area = Group.Area, ShiftId = Group.ShiftId, Sku, Reference, ReceiptId = result.Id });
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
        public string Mode { get; set; } = "list";
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
    public sealed record CaptureProductMeta(string Unit);
    public sealed record CaptureReceiptItem(Guid CaptureId, DateOnly Date, ProductionDailyArea Area, Guid ShiftId,
        string Sku, decimal Quantity, string Unit);
    public sealed record CaptureReceipt(Guid Id, DateTimeOffset RecordedAt, string Responsible,
        IReadOnlyList<CaptureReceiptItem> Items);
}
