using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Web.Production;

namespace WarehouseEPI.Web.Pages.Operations.Production;

public sealed partial class IndexModel
{
    public ProductionMatrixPreview? MatrixPreview { get; private set; }
    public Dictionary<(Guid Product, ProductionDailyArea Area), ProductionAvailableProduct> MatrixAvailable { get; } = [];
    public Dictionary<(Guid Product, ProductionDailyArea Area), decimal> MatrixRegistered { get; } = [];
    public bool MatrixRestored { get; private set; }
    public string? MatrixShiftName { get; private set; }

    private async Task LoadMatrixAsync(bool preserve, CancellationToken token)
    {
        Group.FocusArea = Group.FocusArea is "0" or "1" or "2" ? Group.FocusArea : "0";
        if (!preserve)
        {
            var chosen = Weeks.FirstOrDefault(x => x.Id == WeekId);
            if (chosen is not null && (Group.Date < chosen.WeekStart || Group.Date > chosen.WeekEnd))
            {
                var offset = Day.HasValue ? ((int)Day.Value.DayOfWeek + 6) % 7 : 0;
                Group.Date = chosen.WeekStart.AddDays(offset);
            }
            if (!ReceiptId.HasValue && !Shifts.Any(x => x.Id == Group.ShiftId)) Group.ShiftId = Shifts.FirstOrDefault()?.Id ?? Guid.Empty;
        }
        MatrixShiftName = await db.ProductionShifts.AsNoTracking().Where(x => x.Id == Group.ShiftId).Select(x => x.Name).SingleOrDefaultAsync(token);
        CaptureWeek = Weeks.SingleOrDefault(x => x.WeekStart <= Group.Date && x.WeekEnd >= Group.Date);
        WeekId = CaptureWeek?.Id;
        CaptureState = !ConfigurationReady ? CapturePresentationState.ConfigurationIncomplete
            : CaptureWeek is null ? CapturePresentationState.MissingWeek
            : CaptureWeek.Status == ProductionScheduleWeekStatus.Closed ? CapturePresentationState.Closed
            : CaptureWeek.Status == ProductionScheduleWeekStatus.Draft ? CapturePresentationState.Draft : CapturePresentationState.Open;
        CaptureContextMessage = CaptureWeek is null ? "No hay una semana programada para esta fecha. Selecciona otra fecha o crea la semana en Programa semanal."
            : CaptureWeek.Status == ProductionScheduleWeekStatus.Draft ? "Esta semana está en borrador. Ábrela desde Programa semanal para empezar a capturar."
            : CaptureWeek.Status == ProductionScheduleWeekStatus.Closed ? "Esta semana está cerrada. Consulta su balance o solicita al administrador que la reabra." : null;
        if (CaptureOpen)
            foreach (var area in Enum.GetValues<ProductionDailyArea>())
                foreach (var item in await captures.GetAvailabilityAsync(Group.Date, area, token: token)) MatrixAvailable[(item.ProductId, area)] = item;
        Available = MatrixAvailable.Values.DistinctBy(x => x.ProductId).OrderBy(x => x.Sku).ToArray();
        if (!preserve) Group.Rows = Available.Take(100).Select(x => new GroupRowInput { ProductId = x.ProductId }).ToList();
        NormalizeMatrixRows();
        await LoadCapturePendingAsync(token);
        foreach (var key in ModelState.Keys.Where(x => x.StartsWith("Group.Rows[", StringComparison.Ordinal)).ToArray()) ModelState.Remove(key);
        var ids = Group.Rows.Select(x => x.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.Sku, Unit = x.BaseUnit.Code, x.IsActive }).ToListAsync(token);
        CaptureProducts = products.ToDictionary(x => x.Id, x => new CaptureProductMeta(x.Unit, x.IsActive));
        foreach (var row in Group.Rows) row.Sku = products.FirstOrDefault(x => x.Id == row.ProductId)?.Sku;
        if (preserve)
            for (var i = 0; i < Group.Rows.Count; i++)
            {
                var row = Group.Rows[i];
                if (string.IsNullOrWhiteSpace(row.Quantity)) continue;
                if (!ProductionQuantityBinder.TryParse(row.Quantity, out var amount)) ModelState.AddModelError($"Group.Rows[{i}].Quantity", texts[ProductionQuantityBinder.Error]);
                else if (amount > 0 && CaptureProducts.GetValueOrDefault(row.ProductId)?.IsActive != true)
                    ModelState.AddModelError($"Group.Rows[{i}].Quantity", texts["El producto ya no está activo. Limpia esta fila para continuar."]);
            }
        // Keep field indexes stable after a POST; initial rows are ordered by SKU.
        if (!preserve) Group.Rows = Group.Rows.OrderBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Area).ToList();
        foreach (var item in await db.ProductionDailyCaptures.AsNoTracking()
            .Where(x => x.EffectiveDate == Group.Date && x.ShiftId == Group.ShiftId && ids.Contains(x.ProductId) && x.Status == ProductionDailyCaptureStatus.Active)
            .GroupBy(x => new { x.ProductId, x.Area }).Select(x => new { x.Key.ProductId, x.Key.Area, Quantity = x.Sum(c => c.Quantity) }).ToListAsync(token))
            MatrixRegistered[(item.ProductId, item.Area)] = item.Quantity;
        if (CaptureOpen && Group.Rows.Count == 0) CaptureEmptyMessage = "No hay pendientes registrados. Agrega un producto para registrar la producción realizada.";
        if (ReceiptId is Guid receiptId)
        {
            var submission = await db.ProductionCaptureSubmissions.AsNoTracking().Include(x => x.Items)
                .ThenInclude(x => x.Capture).ThenInclude(x => x.Product).ThenInclude(x => x.BaseUnit)
                .SingleOrDefaultAsync(x => x.Id == receiptId, token);
            if (submission?.Items.Count > 0 && submission.Items.All(x => x.Capture.EffectiveDate == Group.Date && x.Capture.ShiftId == Group.ShiftId))
            {
                var actor = await db.Users.AsNoTracking().Where(x => x.Id == submission.ResponsibleUserId).Select(x => x.FullName).SingleAsync(token);
                Receipt = new(submission.Id, submission.OperationId, submission.RecordedAt, actor, submission.Items
                    .OrderBy(x => x.Capture.Product.Sku).ThenBy(x => x.Capture.Area).Select(x => new CaptureReceiptItem(x.Capture.Id,
                        x.Capture.EffectiveDate, x.Capture.Area, x.Capture.ShiftId, x.Capture.Product.Sku, x.Capture.Quantity, x.Capture.Product.BaseUnit.Code)).ToArray());
            }
        }
    }

    private void NormalizeMatrixRows()
    {
        var expanded = new List<GroupRowInput>();
        foreach (var product in Group.Rows.GroupBy(x => x.ProductId))
            foreach (var area in Enum.GetValues<ProductionDailyArea>())
                expanded.Add(product.FirstOrDefault(x => x.Area == area) ?? new GroupRowInput { ProductId = product.Key, Area = area });
        Group.Rows = expanded;
    }

    private bool ValidMatrixShape() => Group.OperationId != Guid.Empty && Group.Date != default && Group.ShiftId != Guid.Empty &&
        Group.Rows.Count <= 300 && Group.Rows.Select(x => x.ProductId).Distinct().Count() <= 100 &&
        Group.Rows.Select(x => (x.ProductId, x.Area)).Distinct().Count() == Group.Rows.Count &&
        Group.Rows.All(x => x.ProductId != Guid.Empty && x.Area.HasValue && Enum.IsDefined(x.Area.Value) &&
            x.Quantity?.Length is not > 128 && x.Notes?.Length is not > 500);

    private void ClearMatrixSecrets()
    {
        Group.Pin = ""; Group.Fingerprint = "";
        ProductionCapture.ClearPins(this); ModelState.Remove("Group.Fingerprint");
    }

    private ProductionMatrixCommand MatrixCommand(bool validate)
    {
        var rows = new List<ProductionMatrixRow>();
        for (var i = 0; i < Group.Rows.Count; i++)
        {
            var row = Group.Rows[i];
            if (string.IsNullOrWhiteSpace(row.Quantity)) continue;
            if (!ProductionQuantityBinder.TryParse(row.Quantity, out var amount))
            {
                if (validate) ModelState.AddModelError($"Group.Rows[{i}].Quantity", texts[ProductionQuantityBinder.Error]);
            }
            else if (amount != 0) rows.Add(new(row.ProductId, row.Area!.Value, amount, row.Notes));
        }
        return new(Group.OperationId, Group.Date, Group.ShiftId, rows, Group.Fingerprint ?? "", Group.Pin ?? "");
    }

    private async Task<IActionResult> EditMatrixAsync(bool add, CancellationToken token)
    {
        ProductionCapture.ValidateOnly(this, nameof(Group));
        ClearMatrixSecrets();
        if (!ModelState.IsValid || !ValidMatrixShape()) return BadRequest();
        if (add)
        {
            var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == Group.AddProductId && x.IsActive, token);
            if (product is null) ModelState.AddModelError(string.Empty, texts["Selecciona un SKU activo del catálogo."]);
            else if (!Group.Rows.Any(x => x.ProductId == product.Id))
            {
                if (Group.Rows.Select(x => x.ProductId).Distinct().Count() >= 100)
                    Group.Rows = Group.Rows.GroupBy(x => x.ProductId).Where(g => g.Any(x => !string.IsNullOrWhiteSpace(x.Quantity) || !string.IsNullOrWhiteSpace(x.Notes))).SelectMany(x => x).ToList();
                if (Group.Rows.Select(x => x.ProductId).Distinct().Count() >= 100) ModelState.AddModelError(string.Empty, texts["Selecciona entre 1 y 100 productos sin repetir."]);
                else Group.Rows.Add(new() { ProductId = product.Id, Area = ProductionDailyArea.Cutting });
            }
            FocusProductId = product?.Id;
        }
        Tab = "capture";
        await LoadAsync(token, true);
        if (Group.Mode == "list")
        {
            foreach (var product in Available)
                if (Group.Rows.Select(x => x.ProductId).Distinct().Count() < 100 && Group.Rows.All(x => x.ProductId != product.ProductId))
                    Group.Rows.Add(new() { ProductId = product.ProductId, Area = ProductionDailyArea.Cutting });
            await LoadMatrixAsync(true, token);
        }
        Group.Rows = Group.Rows.OrderBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Area).ToList();
        foreach (var key in ModelState.Keys.Where(x => x.StartsWith("Group.Rows[", StringComparison.Ordinal)).ToArray()) ModelState.Remove(key);
        return Page();
    }

    private async Task<IActionResult> ProcessMatrixAsync(bool confirm, CancellationToken token)
    {
        Tab = "capture";
        ProductionCapture.ValidateOnly(this, nameof(Group));
        if (!ValidMatrixShape()) { ClearMatrixSecrets(); return BadRequest(); }
        if (!await RequireConfigurationAsync(token)) { ClearMatrixSecrets(); await LoadAsync(token, true); return Page(); }
        var command = MatrixCommand(true);
        if (ModelState.IsValid)
        {
            if (confirm)
            {
                var result = await captures.ConfirmMatrixAsync(command, token);
                RoleWarning = result.Status == ProductionDailyCommandStatus.RoleNotAllowed;
                if (result.Success) return RedirectToPage(new { Tab, Day = Group.Date.ToString("yyyy-MM-dd"), ShiftId = Group.ShiftId, ReceiptId = result.Id });
                foreach (var error in result.Errors ?? ["La operación ya se utilizó con otros datos."])
                    ModelState.AddModelError(string.Empty, ProductionDailyText.Message(texts, error));
            }
            MatrixPreview = await captures.PreviewMatrixAsync(command, token);
        }
        ClearMatrixSecrets();
        Group.Fingerprint = MatrixPreview?.Fingerprint ?? "";
        await LoadAsync(token, true);
        return Page();
    }

    private async Task<IActionResult> RestoreMatrixAsync(CancellationToken token)
    {
        ProductionCapture.ValidateOnly(this, nameof(Group)); ClearMatrixSecrets();
        if (!ModelState.IsValid || !ValidMatrixShape() || Group.Rows.Count == 0) return BadRequest();
        var command = MatrixCommand(true);
        var recorded = await captures.FindRecordedMatrixAsync(command, token);
        if (recorded?.Success == true && ModelState.IsValid)
            return RedirectToPage(new { Tab = "capture", Day = Group.Date.ToString("yyyy-MM-dd"), ShiftId = Group.ShiftId, ReceiptId = recorded.Id });
        if (recorded is not null)
        {
            RestoreConflict = true;
            ModelState.AddModelError(string.Empty, texts["Esta operación ya fue registrada con otros datos. Consulta el historial; el borrador se conserva."]);
        }
        // Raw invalid quantities remain in the inputs, but client recovery must not reuse a review.
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
}
