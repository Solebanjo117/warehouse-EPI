using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Security;

public sealed class CycleCountPreparationProtector(IDataProtectionProvider provider)
{
    private readonly ITimeLimitedDataProtector protector = provider.CreateProtector("WarehouseEPI.CycleCountPreparation.v1").ToTimeLimitedDataProtector();
    public string Protect(CycleCountPreparation value) => protector.Protect(JsonSerializer.Serialize(value), TimeSpan.FromHours(12));
    public bool TryUnprotect(string? token, out CycleCountPreparation? value)
    {
        value = null;
        try { value = JsonSerializer.Deserialize<CycleCountPreparation>(protector.Unprotect(token ?? string.Empty)); return value is not null; }
        catch { return false; }
    }
}
