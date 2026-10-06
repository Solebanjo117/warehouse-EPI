using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;

namespace WarehouseEPI.Infrastructure.Imports;

public sealed record WipTransferSourceRow(int Number, Guid OperationId, DateOnly? Date, string Sku,
    string Unit, decimal? Quantity, string Area, string Status, bool Repeated, IReadOnlyList<string> Errors);

public sealed record WipTransferFile(string Name, string Hash, IReadOnlyList<WipTransferSourceRow> Rows);

public static class WipTransferSpreadsheetReader
{
    public const int MaxRows = 2000;
    public const long MaxBytes = 10 * 1024 * 1024;
    private static readonly string[] Headers = ["Transfer Date", "Part Number", "Description", "UOM",
        "Qty Requested", "Qty Delivered", "Delivery WIP Location", "Status"];

    public static WipTransferFile Read(Stream stream, string fileName)
    {
        using var data = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (data.Length + read > MaxBytes) throw new InvalidDataException("El archivo no puede superar 10 MB.");
            data.Write(buffer, 0, read);
        }
        var hash = Convert.ToHexString(SHA256.HashData(data.ToArray()));
        data.Position = 0;
        using (var zip = new ZipArchive(data, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (zip.Entries.Count > 2000 || zip.Entries.Sum(x => x.Length) > 64 * 1024 * 1024)
                throw new InvalidDataException("El contenido descomprimido del archivo es demasiado grande.");
        }
        data.Position = 0;
        using var workbook = new XLWorkbook(data);
        if (!workbook.TryGetWorksheet("TRANSFER LOG", out var sheet))
            throw new InvalidDataException("El archivo debe contener la hoja TRANSFER LOG.");
        for (var column = 1; column <= Headers.Length; column++)
            if (!string.Equals(sheet.Cell(1, column).GetString().Trim(), Headers[column - 1], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"La columna {column} de TRANSFER LOG debe llamarse {Headers[column - 1]}.");
        var last = sheet.LastRowUsed(XLCellsUsedOptions.Contents)?.RowNumber() ?? 1;
        if (last > MaxRows + 1) throw new InvalidDataException($"El reporte admite hasta {MaxRows} filas.");
        var rows = new List<WipTransferSourceRow>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var number = 2; number <= last; number++)
        {
            string Cell(int col) => sheet.Cell(number, col).CachedValue.ToString(CultureInfo.InvariantCulture).Trim();
            if (Enumerable.Range(1, 8).All(col => Cell(col).Length == 0)) continue;
            var errors = new List<string>();
            // UOM in the supplied report is an XLOOKUP formula. Read only its saved value;
            // never evaluate workbook formulas or resolve linked files. Catalog validation
            // checks the saved unit against the product's current base unit.
            if (new[] { 1, 2, 6, 7, 8 }.Any(col => sheet.Cell(number, col).HasFormula))
                errors.Add("Fecha, producto, cantidad, destino y estado deben contener valores, no fórmulas.");
            DateOnly? date = !sheet.Cell(number, 1).HasFormula && sheet.Cell(number, 1).TryGetValue<DateTime>(out var instant)
                ? DateOnly.FromDateTime(instant) : null;
            if (date is null || date.Value.Year < 2000 || date.Value.Year > 2100)
                errors.Add("La fecha no es válida. Corrige Transfer Date en el archivo.");
            decimal? quantity = !sheet.Cell(number, 6).HasFormula && sheet.Cell(number, 6).TryGetValue<decimal>(out var qty) ? qty : null;
            if (quantity is null || quantity <= 0 || quantity > 99_999_999_999_999.9999m || decimal.Round(quantity.Value, 4) != quantity)
                errors.Add("Qty Delivered debe ser positiva y tener como máximo cuatro decimales.");
            var sku = Normalize(Cell(2));
            var unit = Normalize(Cell(4));
            var open = unit.LastIndexOf('(');
            if (open >= 0 && unit.EndsWith(')')) unit = unit[(open + 1)..^1].Trim();
            var area = Cell(7);
            var status = Normalize(Cell(8));
            if (sku.Length == 0) errors.Add("Falta Part Number. Corrige el producto en el archivo.");
            if (unit.Length == 0) errors.Add("Falta UOM. Corrige la unidad en el archivo.");
            if (status.Length > 0 && status is not ("ISSUED" or "RECEIVED" or "PARTIALLY RECEIVED"))
                errors.Add("El estado no acredita una entrega. Corrige el archivo o excluye la fila.");
            // Independent of filename, row position, user and location corrections: repeated uploads
            // (including overlapping reports) reuse the same durable movement operation IDs.
            var identity = JsonSerializer.Serialize(new[] { date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? Cell(1),
                sku, unit, quantity?.ToString("G29", CultureInfo.InvariantCulture) ?? Cell(6), Normalize(area) });
            var occurrence = occurrences.GetValueOrDefault(identity) + 1;
            occurrences[identity] = occurrence;
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"wip-transfer-v1:{identity}:{occurrence}"))[..16]);
            rows.Add(new(number, id, date, sku, unit, quantity, area, status, occurrence > 1, errors));
        }
        if (rows.Count == 0) throw new InvalidDataException("TRANSFER LOG no contiene entregas.");
        return new(Path.GetFileName(fileName), hash, rows);
    }

    internal static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
