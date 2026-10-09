using System.Globalization;
using System.Numerics;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Infrastructure.Labels;

public sealed record LabelSeriesRequest(string FieldKey, string? End);
public sealed record LabelBatchResult(IReadOnlyList<LabelRenderDocument> Documents,
    IReadOnlyDictionary<string, string> FieldErrors, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public sealed partial class LabelDocumentService
{
    public static IReadOnlyList<LabelFieldDefinition> SeriesFields(LabelDesignDocumentV1? design) =>
        design?.Fields.Where(field => field.Type is LabelFieldType.Text or LabelFieldType.Number &&
            design.Elements.Any(element => element.Binding == field.Key)).ToArray() ?? [];

    public LabelBatchResult RenderBatch(LabelTemplateVersion version, OperationalProductResult product,
        IReadOnlyDictionary<string, string> submitted, int copies, LabelSeriesRequest? series = null)
    {
        if (series is null || string.IsNullOrEmpty(series.FieldKey))
        {
            var single = Render(version, product, submitted, copies);
            return new(single.Document is null ? [] : [single.Document], new Dictionary<string, string>(), single.Errors, single.Warnings);
        }

        var errors = new Dictionary<string, string>();
        var field = SeriesFields(LabelDesignSerializer.Deserialize(version.DesignJson)).SingleOrDefault(x => x.Key == series.FieldKey);
        if (field is null) errors["SeriesField"] = "Selecciona un campo de texto o número utilizado por la plantilla.";
        var startText = submitted.GetValueOrDefault(series.FieldKey)?.Trim() ?? "";
        var endText = series.End?.Trim() ?? "";
        static bool Parse(string value, out BigInteger number)
        {
            number = 0;
            return value.Length is > 0 and <= 200 && value.All(c => c is >= '0' and <= '9') &&
                BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }
        var startValid = Parse(startText, out var start);
        var endValid = Parse(endText, out var end);
        if (!startValid) errors["SeriesField"] = "El valor inicial debe ser un entero no negativo, sin prefijos ni decimales.";
        if (!endValid) errors["SeriesEnd"] = "El valor final debe ser un entero no negativo, sin prefijos ni decimales.";
        if (copies is < 1 or > 100) errors["Copies"] = "Las copias deben estar entre 1 y 100.";
        if (startValid && endValid)
        {
            if (end < start) errors["SeriesEnd"] = "El valor final debe ser mayor o igual al inicial.";
            else if ((end - start + 1) * copies > 100) errors["SeriesEnd"] = "La serie no puede superar 100 etiquetas, incluidas las copias.";
        }
        if (errors.Count > 0) return new([], errors, [], []);

        var documents = new List<LabelRenderDocument>();
        var warnings = new List<string>();
        for (var number = start; number <= end; number++)
        {
            var value = number.ToString(CultureInfo.InvariantCulture);
            if (field!.Type == LabelFieldType.Text) value = value.PadLeft(startText.Length, '0');
            var values = new Dictionary<string, string>(submitted, StringComparer.Ordinal) { [series.FieldKey] = value };
            var result = Render(version, product, values, copies);
            if (result.Document is null || result.Errors.Count > 0)
                return new([], errors, result.Errors, result.Warnings);
            documents.Add(result.Document);
            warnings.AddRange(result.Warnings);
        }
        return new(documents, errors, [], warnings.Distinct().ToArray());
    }
}
