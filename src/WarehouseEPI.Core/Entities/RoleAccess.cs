namespace WarehouseEPI.Core.Entities;

/// <summary>Fixed roles and operational permissions shared by the web and write services.</summary>
public static class RoleAccess
{
    public const string Admin = "ADMIN";
    public const string Operator = "OPERATOR";
    public const string Production = "PRODUCTION";
    public const string WarehouseWarning = "Este NIP pertenece a Producción. Para registrar operaciones de almacén usa un NIP de Operador o Administrador.";
    public const string ProductionWarning = "Este NIP pertenece a Operador. Para registrar producción usa un NIP de Producción o Administrador.";
    public static bool IsKnown(string? role) => role is Admin or Operator or Production;
    public static bool CanOperateWarehouse(string? role) => role is Admin or Operator;
    public static bool CanCaptureProduction(string? role) => role is Admin or Production;
    public static string Name(string? role) => role switch
    {
        Admin => "Administrador", Operator => "Operador", Production => "Producción", _ => ""
    };
}
