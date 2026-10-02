using WarehouseEPI.Web.Production;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Operations.Production;

public sealed partial class IndexModel
{
    [BindProperty(SupportsGet = true)] public string? Tab { get; set; } = "balance";
    [BindProperty] public GroupInput Group { get; set; } = new();
    public IReadOnlyList<ProductionAvailableProduct> Available { get; private set; } = [];
    public Dictionary<(Guid Product, ProductionDailyArea Area), decimal> CapturePending { get; } = [];

    private async Task LoadCapturePendingAsync(CancellationToken token)
    {
        if (CaptureWeek is null || Group.Rows.Count == 0) return;
        var ids = Group.Rows.Select(x => x.ProductId).ToHashSet();
        var balance = await balances.GetAsync(CaptureWeek.Id, token);
        foreach (var row in balance!.Rows.Where(x => x.Date == Group.Date && ids.Contains(x.ProductId)))
            foreach (var area in new[] { row.Cutting, row.Sewing, row.ReadyToPack })
                CapturePending[(row.ProductId, area.Area)] = area.SignedPending;
    }
    public ProductionCaptureGroupPreview? GroupPreview { get; private set; }
    public ProductionScheduleWeekView? CaptureWeek { get; private set; }
    public string? CaptureContextMessage { get; private set; }
    public string? CaptureEmptyMessage { get; private set; }
    public Dictionary<ProductionDailyArea, int> OtherAvailableAreas { get; } = [];
    public IReadOnlyDictionary<Guid, decimal> RegisteredInShift { get; private set; } = new Dictionary<Guid, decimal>();
    public IReadOnlyDictionary<Guid, CaptureProductMeta> CaptureProducts { get; private set; } = new Dictionary<Guid, CaptureProductMeta>();
    [BindProperty(SupportsGet = true)] public Guid? ReceiptId { get; set; }
    public CaptureReceipt? Receipt { get; private set; }
    public CapturePresentationState CaptureState { get; private set; }
    public bool CaptureOpen => CaptureState == CapturePresentationState.Open;
    public bool RestoreConflict { get; private set; }

    private async Task LoadGroupAsync(CancellationToken token)
    {
        if (Tab is not ("capture" or "balance" or "history")) Tab = "balance";
        if (Tab != "capture") return;
        var handler = RouteData.Values["handler"]?.ToString() ?? Request.Query["handler"].ToString();
        var isGroupPost = HttpMethods.IsPost(Request.Method) && handler is "GroupPreview" or "GroupConfirm" or "GroupAdd" or "GroupFilter" or "GroupMode" or "GroupRestore";
        if (!isGroupPost)
        {
            var week = Weeks.SingleOrDefault(x => x.Id == WeekId);
            Group.Date = Day ?? (week is not null && (Today < week.WeekStart || Today > week.WeekEnd) ? week.WeekStart : Today);
            Group.Area = Area ?? ProductionDailyArea.Cutting;
            Group.ShiftId = ShiftId ?? Shifts.FirstOrDefault()?.Id ?? Guid.Empty;
        }
        Group.Mode = Group.Mode == "quick" ? "quick" : "list";
        if (!isGroupPost || Group.AllAreas)
        {
            Group.AllAreas = true;
            await LoadMatrixAsync(isGroupPost, token);
            return;
        }
        CaptureWeek = Weeks.SingleOrDefault(x => x.WeekStart <= Group.Date && x.WeekEnd >= Group.Date);
        CaptureState = !ConfigurationReady ? CapturePresentationState.ConfigurationIncomplete
            : CaptureWeek is null ? CapturePresentationState.MissingWeek
            : CaptureWeek.Status == ProductionScheduleWeekStatus.Closed ? CapturePresentationState.Closed
            : CaptureWeek.Status == ProductionScheduleWeekStatus.Draft ? CapturePresentationState.Draft
            : CapturePresentationState.Open;
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
            Group.Rows = kept.OrderBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ToList();
            ModelState.Clear();
        }
        var rowIds = Group.Rows.Select(x => x.ProductId).Distinct().ToArray();
        await LoadCapturePendingAsync(token);
        CaptureProducts = await db.Products.AsNoTracking().Where(x => rowIds.Contains(x.Id))
            .Select(x => new { x.Id, Unit = x.BaseUnit.Code, x.IsActive })
            .ToDictionaryAsync(x => x.Id, x => new CaptureProductMeta(x.Unit, x.IsActive), token);
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
                Receipt = new CaptureReceipt(submission.Id, submission.OperationId, submission.RecordedAt, actor,
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

    public async Task<IActionResult> OnPostGroupRestoreAsync(CancellationToken token)
    {
        if (Group.AllAreas) return await RestoreMatrixAsync(token);
        ProductionCapture.ValidateOnly(this, nameof(Group));
        Group.Pin = "";
        Group.Fingerprint = "";
        ProductionCapture.ClearPins(this);
        ModelState.Remove("Group.Fingerprint");
        if (!ModelState.IsValid || Group.OperationId == Guid.Empty || Group.Date == default ||
            !Enum.IsDefined(Group.Area) || Group.ShiftId == Guid.Empty || Group.Rows.Count is 0 or > 100 ||
            Group.Rows.Any(x => x.ProductId == Guid.Empty || x.Notes?.Length > 500 || x.Quantity?.Length > 128) ||
            Group.Rows.Select(x => x.ProductId).Distinct().Count() != Group.Rows.Count)
            return BadRequest(texts["El borrador no es válido. Conserva los datos y vuelve a la captura."].Value);

        var rows = new List<ProductionCaptureRow>();
        var invalidQuantity = false;
        foreach (var row in Group.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.Quantity)) continue;
            if (ProductionQuantityBinder.TryParse(row.Quantity, out var amount))
            {
                if (amount != 0) rows.Add(new(row.ProductId, amount, row.Notes));
            }
            else invalidQuantity = true;
        }
        var recorded = await captures.FindRecordedGroupAsync(new(Group.OperationId, Group.Date, Group.Area, Group.ShiftId, rows), token);
        if (recorded?.Success == true && !invalidQuantity)
            return RedirectToPage(new { Tab = "capture", Day = Group.Date.ToString("yyyy-MM-dd"), Area = Group.Area, ShiftId = Group.ShiftId, ReceiptId = recorded.Id });
        if (recorded is not null)
        {
            RestoreConflict = true;
            ModelState.AddModelError(string.Empty, texts["Esta operación ya fue registrada con otros datos. Consulta el historial; el borrador se conserva."]);
        }
        if (Group.ImportLegacy)
        {
            Group.LegacySource = $"warehouse-epi.production-capture.v1:{Group.Date:yyyy-MM-dd}:{Group.Area}:{Group.ShiftId}";
            Group.LegacyOperationId = Group.OperationId;
            foreach (var row in Group.Rows) row.Area = Group.Area;
            if (recorded is null) Group.OperationId = Guid.NewGuid();
            Group.AllAreas = true;
            Group.FocusArea = ((int)Group.Area).ToString();
            if (recorded is null) ModelState.Clear();
            Tab = "capture"; Day = Group.Date; ShiftId = Group.ShiftId;
            await LoadAsync(token, true);
            if (Shifts.All(x => x.Id != Group.ShiftId))
            {
                RestoreConflict = true;
                ModelState.AddModelError(string.Empty, texts["El turno del borrador ya no está disponible. Conserva los datos y consulta al administrador."]);
            }
            MatrixRestored = !RestoreConflict;
            return Page();
        }
        var ids = Group.Rows.Select(x => x.ProductId).ToArray();
        var activeIds = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id) && x.IsActive).Select(x => x.Id).ToArrayAsync(token);
        for (var i = 0; i < Group.Rows.Count; i++)
        {
            // Never trust the stored SKU; LoadGroupAsync reloads it from the catalog.
            ModelState.Remove($"Group.Rows[{i}].Sku");
            if (!activeIds.Contains(Group.Rows[i].ProductId))
                ModelState.AddModelError($"Group.Rows[{i}].Quantity", texts["El producto ya no está activo. Limpia esta fila para continuar."]);
        }
        Tab = "capture";
        Day = Group.Date;
        Area = Group.Area;
        ShiftId = Group.ShiftId;
        WeekId = (await schedules.ListWeeksAsync(token)).FirstOrDefault(x => x.WeekStart <= Group.Date && x.WeekEnd >= Group.Date)?.Id;
        await LoadAsync(token, true);
        if (Shifts.All(x => x.Id != Group.ShiftId))
        {
            RestoreConflict = true;
            ModelState.AddModelError(string.Empty, texts["El turno del borrador ya no está disponible. Conserva los datos y consulta al administrador."]);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostGroupModeAsync(CancellationToken token)
    {
        if (Group.AllAreas) return await EditMatrixAsync(false, token);
        Group.Pin = ""; ProductionCapture.ClearPins(this);
        Group.Fingerprint = "";
        Tab = "capture";
        await LoadAsync(token, true);
        return Page();
    }

    public async Task<IActionResult> OnGetDailyProductsAsync(DateOnly date, ProductionDailyArea area,
        string? q, int? category, int offset, bool allAreas, CancellationToken token)
    {
        // This GET only validates lookup parameters, not the unrelated capture/PIN form.
        var parameters = new[] { "date", "area", "q", "category", "offset" };
        if (date == default || ModelState.Any(x => parameters.Contains(x.Key, StringComparer.OrdinalIgnoreCase) && x.Value?.Errors.Count > 0)
            || !(await ProductionDailySetup.LoadAsync(schedules, db, token)).IsReady)
            return new JsonResult(Array.Empty<ProductionProductSuggestionGroup>());
        return new JsonResult(allAreas ? await captures.SearchMatrixProductsAsync(date, q, category, offset, token)
            : await captures.SearchDailyProductsAsync(date, area, q, category, offset, token));
    }

    public async Task<IActionResult> OnPostGroupAddAsync(CancellationToken token)
    {
        if (Group.AllAreas) return await EditMatrixAsync(true, token);
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
        Group.Rows = Group.Rows.OrderBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostGroupFilterAsync(CancellationToken token)
    {
        if (Group.AllAreas) return await EditMatrixAsync(false, token);
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
        if (Group.AllAreas) return await ProcessMatrixAsync(confirm, token);
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
                RoleWarning = result.Status == ProductionDailyCommandStatus.RoleNotAllowed;
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
        public bool AllAreas { get; set; }
        public bool ImportLegacy { get; set; }
        public string FocusArea { get; set; } = "0";
        public string? LegacySource { get; set; }
        public Guid? LegacyOperationId { get; set; }
        public string Mode { get; set; } = "list";
        public bool OnlyWithQuantity { get; set; }
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
        public ProductionDailyArea? Area { get; set; }
        public Guid ProductId { get; set; }
        public string? Sku { get; set; }
        public string? Quantity { get; set; }
        public string? Notes { get; set; }
    }
    public sealed record CaptureProductMeta(string Unit, bool IsActive);
    public sealed record CaptureReceiptItem(Guid CaptureId, DateOnly Date, ProductionDailyArea Area, Guid ShiftId,
        string Sku, decimal Quantity, string Unit);
    public enum CapturePresentationState { Open, Closed, Draft, MissingWeek, ConfigurationIncomplete }
    public sealed record CaptureReceipt(Guid Id, Guid OperationId, DateTimeOffset RecordedAt, string Responsible,
        IReadOnlyList<CaptureReceiptItem> Items);
}
