using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public static class CycleCountPresentation
{
    /// <summary>
    /// Traduce cualquier resultado a un mensaje operativo. Los estados sin
    /// <see cref="CycleCountResult.ValidationErrors"/> también deben explicarse: de lo
    /// contrario la pantalla muestra una alerta vacía y el operador queda sin salida.
    /// </summary>
    public static string StatusMessage(CycleCountResult result) => StatusMessage(result.Status, result.ValidationErrors);
    public static string StatusMessage(CycleCountResult result, IStringLocalizer<OperationsTexts> texts) => StatusMessage(result.Status, result.ValidationErrors, texts);

    public static string StatusMessage(CycleCountBatchResult result) => StatusMessage(result.Status, result.Errors ?? []);
    public static string StatusMessage(CycleCountBatchResult result, IStringLocalizer<OperationsTexts> texts) => StatusMessage(result.Status, result.Errors ?? [], texts);

    private static string StatusMessage(CycleCountStatus status, IReadOnlyList<string> errors) => status switch
    {
        CycleCountStatus.InvalidPin => "NIP no válido.",
        CycleCountStatus.BalanceChanged => "El saldo cambió; solicita e inicia un reconteo.",
        CycleCountStatus.RequiresLocationSharingConfirmation => "La ubicación contiene otros productos. Confirma expresamente cada asignación compartida y vuelve a autorizar.",
        CycleCountStatus.NotFound => "La campaña o la ubicación ya no está disponible. Vuelve a la campaña y ábrela de nuevo.",
        CycleCountStatus.InvalidState => "Otra persona cambió el estado de esta ubicación. Vuelve a la campaña para ver cómo quedó.",
        CycleCountStatus.IdempotencyConflict => "Esta operación ya se registró con otros datos. Vuelve a la campaña antes de reintentar.",
        _ => errors.Count != 0
            ? string.Join(' ', errors)
            : "No fue posible completar la operación. Vuelve a intentarlo."
    };
    private static string StatusMessage(CycleCountStatus status, IReadOnlyList<string> errors, IStringLocalizer<OperationsTexts> texts) => status switch
    {
        CycleCountStatus.InvalidPin => texts["NIP no válido."],
        CycleCountStatus.BalanceChanged => texts["El saldo cambió; solicita e inicia un reconteo."],
        CycleCountStatus.RequiresLocationSharingConfirmation => texts["La ubicación contiene otros productos. Confirma expresamente cada asignación compartida y vuelve a autorizar."],
        CycleCountStatus.NotFound => texts["La campaña o la ubicación ya no está disponible. Vuelve a la campaña y ábrela de nuevo."],
        CycleCountStatus.InvalidState => texts["Otra persona cambió el estado de esta ubicación. Vuelve a la campaña para ver cómo quedó."],
        CycleCountStatus.IdempotencyConflict => texts["Esta operación ya se registró con otros datos. Vuelve a la campaña antes de reintentar."],
        _ => errors.Count != 0 ? string.Join(' ', errors.Select(error => texts[error].Value)) : texts["No fue posible completar la operación. Vuelve a intentarlo."]
    };

    public static string CampaignStatusLabel(CycleCountCampaignStatus status) => status switch
    {
        CycleCountCampaignStatus.Draft => "Borrador",
        CycleCountCampaignStatus.Released => "Lista para contar",
        CycleCountCampaignStatus.InProgress => "En conteo",
        CycleCountCampaignStatus.UnderReview => "Requiere revisión",
        CycleCountCampaignStatus.Completed => "Completada",
        CycleCountCampaignStatus.Cancelled => "Cancelada",
        _ => status.ToString()
    };

    public static string CampaignStatusClass(CycleCountCampaignStatus status) => status switch
    {
        CycleCountCampaignStatus.Draft => "text-bg-secondary",
        CycleCountCampaignStatus.Released => "text-bg-primary",
        CycleCountCampaignStatus.InProgress => "text-bg-info",
        CycleCountCampaignStatus.UnderReview => "text-bg-warning",
        CycleCountCampaignStatus.Completed => "text-bg-success",
        CycleCountCampaignStatus.Cancelled => "text-bg-dark",
        _ => "text-bg-secondary"
    };

    public static string LocationStatusLabel(CycleCountLocationStatus status) => status switch
    {
        CycleCountLocationStatus.Pending => "Pendiente",
        CycleCountLocationStatus.Counting => "En conteo",
        CycleCountLocationStatus.UnderReview => "Requiere revisión",
        CycleCountLocationStatus.RecountRequested => "Reconteo solicitado",
        CycleCountLocationStatus.Stale => "Saldo cambió",
        CycleCountLocationStatus.Completed => "Completada",
        CycleCountLocationStatus.Cancelled => "Cancelada",
        _ => status.ToString()
    };

    public static string LocationStatusClass(CycleCountLocationStatus status) => status switch
    {
        CycleCountLocationStatus.Pending => "text-bg-secondary",
        CycleCountLocationStatus.Counting => "text-bg-info",
        CycleCountLocationStatus.UnderReview => "text-bg-warning",
        CycleCountLocationStatus.RecountRequested => "text-bg-primary",
        CycleCountLocationStatus.Stale => "text-bg-danger",
        CycleCountLocationStatus.Completed => "text-bg-success",
        CycleCountLocationStatus.Cancelled => "text-bg-dark",
        _ => "text-bg-secondary"
    };
}
