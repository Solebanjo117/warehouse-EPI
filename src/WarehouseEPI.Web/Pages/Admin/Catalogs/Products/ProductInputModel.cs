using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

public interface IProductFormPage
{
    ProductInputModel Input { get; }
    IReadOnlyList<SelectListItem> Units { get; }
    IReadOnlyList<SelectListItem> Types { get; }
    IReadOnlyList<SelectListItem> Classes { get; }
    ProductEntryLocationOption? SelectedEntryLocation { get; }
    MaterialWipInputModel Wip { get; }
    MaterialWipDefaultsView? WipConfiguration { get; }
}

public sealed class MaterialWipInputModel
{
    public Guid OperationId { get; set; } = Guid.NewGuid();
    public uint ExpectedVersion { get; set; }
    public List<MaterialWipRuleInputModel> Rules { get; set; } = [];
    [StringLength(500)] public string? Reason { get; set; }
    [RegularExpression("^[0-9]{4,8}$")] public string? Pin { get; set; }
}

public sealed class MaterialWipRuleInputModel
{
    public Guid? StageId { get; set; }
    public string? TargetKey { get; set; }
    public string? TargetLabel { get; set; }
}

public sealed record ProductEntryLocationOption(
    Guid Id,
    string Code,
    string? Description,
    bool IsAvailable);

public sealed class ProductInputModel
{
    public Guid Id { get; set; }
    [Required(ErrorMessage = "El SKU es obligatorio.")]
    [StringLength(60, ErrorMessage = "El SKU no puede superar 60 caracteres.")]
    public string Sku { get; set; } = string.Empty;
    public string? Description { get; set; }
    [StringLength(120, ErrorMessage = "La referencia no puede superar 120 caracteres.")]
    public string? ExternalReference { get; set; }
    public short? ProductTypeId { get; set; }
    public short? ProductClassId { get; set; }
    [Range(1, short.MaxValue, ErrorMessage = "Seleccione una unidad base.")]
    public short BaseUnitId { get; set; }
    [Range(typeof(decimal), "0", "99999999999999.9999", ErrorMessage = "El stock mínimo debe ser mayor o igual que cero.")]
    public decimal MinimumStock { get; set; }
    public Guid? DefaultEntryLocationId { get; set; }
    public bool IsActive { get; set; } = true;
}
