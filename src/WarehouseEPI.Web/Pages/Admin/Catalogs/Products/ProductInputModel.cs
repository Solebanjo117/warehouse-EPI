using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

public interface IProductFormPage
{
    ProductInputModel Input { get; }
    IReadOnlyList<SelectListItem> Units { get; }
    IReadOnlyList<SelectListItem> Types { get; }
    IReadOnlyList<SelectListItem> Classes { get; }
}

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
    public bool TracksLots { get; set; }
    public bool TracksExpiration { get; set; }
    public bool IsActive { get; set; } = true;
}
