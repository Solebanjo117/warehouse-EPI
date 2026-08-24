using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public static class CycleCountPresentation
{
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
