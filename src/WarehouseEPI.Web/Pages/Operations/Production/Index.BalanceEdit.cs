using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Operations.Production;

public sealed partial class IndexModel
{
    public sealed record BalanceCellInput(Guid ProductId, ProductionDailyArea Area, int Shift, string? Observed, string? Requested);
    public sealed record BalanceEditInput(Guid OperationId, Guid WeekId, DateOnly Date, List<BalanceCellInput>? Cells,
        string? Reason, string? Fingerprint, string? Pin);

    private static ProductionBalanceEditCommand? BalanceCommand(BalanceEditInput? input)
    {
        // Validate the JSON body here; unrelated capture forms share this PageModel.
        if (input?.Cells is null || input.Cells.Count is < 1 or > 100) return null;
        var cells = new List<ProductionBalanceCell>();
        foreach (var cell in input.Cells)
        {
            if (cell is null) return null;
            bool Parse(string? value, out decimal quantity)
            {
                quantity = 0;
                return value is not null && Regex.IsMatch(value, @"^\d{1,14}(?:\.\d{1,4})?$") &&
                    decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quantity);
            }
            if (!Parse(cell.Requested, out var requested) || !Parse(cell.Observed, out var observed)) return null;
            cells.Add(new(cell.ProductId, cell.Area, cell.Shift, observed, requested));
        }
        return new(input.OperationId, input.WeekId, input.Date, cells, input.Reason, input.Fingerprint ?? "", input.Pin ?? "");
    }

    public async Task<IActionResult> OnPostBalanceEditPreviewAsync([FromBody] BalanceEditInput? input, CancellationToken token)
    {
        var command = BalanceCommand(input);
        if (command is null) return new JsonResult(new { canConfirm = false, errors = new[] { texts["Indica una cantidad válida. Solo punto decimal."].Value } });
        var preview = await captures.PreviewBalanceEditAsync(command with { Pin = "" }, token);
        return new JsonResult(preview with { Errors = preview.Errors.Select(BalanceError).ToArray(),
            Cells = preview.Cells.Select(x => x with { Errors = x.Errors.Select(BalanceError).ToArray() }).ToArray() });
    }

    public async Task<IActionResult> OnPostBalanceEditConfirmAsync([FromBody] BalanceEditInput? input, CancellationToken token)
    {
        var command = BalanceCommand(input);
        if (command is null) return BadRequest();
        var result = await captures.ConfirmBalanceEditAsync(command, token);
        return new JsonResult(new { result.Success, Errors = result.Errors?.Select(BalanceError).ToArray(), status = result.Status.ToString() });
    }

    private string BalanceError(string error)
    {
        var localized = texts[error];
        if (!localized.ResourceNotFound) return localized.Value;
        var separator = error.IndexOf(": ", StringComparison.Ordinal);
        return separator < 0 ? error : error[..(separator + 2)] + texts[error[(separator + 2)..]].Value;
    }
}
