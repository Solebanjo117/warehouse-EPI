using WarehouseEPI.Core.Entities;
namespace WarehouseEPI.Infrastructure.Production;
public static class ProductionStateLabels
{
    public static string Label(ProductionWorkOrderStatus state) => state switch
    {
        ProductionWorkOrderStatus.Draft => "Borrador", ProductionWorkOrderStatus.Released => "Liberada",
        ProductionWorkOrderStatus.InProgress => "En fabricación", ProductionWorkOrderStatus.Paused => "Pausada",
        ProductionWorkOrderStatus.PrincipalClosed => "Fabricación principal cerrada", ProductionWorkOrderStatus.Closed => "Cierre definitivo",
        ProductionWorkOrderStatus.Cancelled => "Cancelada", _ => state.ToString()
    };
}
