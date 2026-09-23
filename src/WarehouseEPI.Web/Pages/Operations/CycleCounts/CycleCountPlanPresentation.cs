using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public static class CycleCountPlanPresentation
{
    public static string FrequencyLabel(CycleCountFrequency value) => value switch
    {
        CycleCountFrequency.Weekly => "Semanal",
        CycleCountFrequency.Biweekly => "Quincenal",
        CycleCountFrequency.Monthly => "Mensual",
        CycleCountFrequency.Quarterly => "Trimestral",
        CycleCountFrequency.Semiannual => "Semestral",
        CycleCountFrequency.Annual => "Anual",
        _ => value.ToString()
    };

    public static string FrequencyLabel(CycleCountFrequency value, IStringLocalizer<OperationsTexts> texts) => texts[FrequencyLabel(value)];
}
