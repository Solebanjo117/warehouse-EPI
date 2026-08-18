using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Branding;

namespace WarehouseEPI.Web.Pages.Admin.Settings;

[Authorize(Policy = "AdminOnly")]
public sealed class BusinessModel(
    WarehouseDbContext dbContext,
    WarehouseSettingsService settingsService,
    BrandingStorage brandingStorage) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        Input = new()
        {
            BusinessName = settings.BusinessName,
            WarehouseName = settings.WarehouseName,
            WarehouseCode = settings.WarehouseCode,
            TimeZoneId = settings.TimeZoneId
        };
        ViewData["LogoUrl"] = settings.LogoFileName is null ? null : $"/branding/logo?v={settings.LogoHash}";
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Input.BusinessName = Input.BusinessName.Trim();
        Input.WarehouseName = Input.WarehouseName.Trim();
        Input.WarehouseCode = Input.WarehouseCode.Trim().ToUpperInvariant();
        Input.TimeZoneId = Input.TimeZoneId.Trim();
        if (!WarehouseClock.IsValidTimeZone(Input.TimeZoneId))
            ModelState.AddModelError("Input.TimeZoneId", "La zona horaria no está disponible en este servidor.");
        if (!ModelState.IsValid) return Page();

        StoredLogo? uploaded = null;
        try
        {
            if (Input.Logo is not null) uploaded = await brandingStorage.SaveAsync(Input.Logo, cancellationToken);
        }
        catch (BrandingValidationException exception)
        {
            ModelState.AddModelError("Input.Logo", exception.Message);
            return Page();
        }

        var settings = await settingsService.GetTrackedAsync(cancellationToken);
        var previousLogo = settings.LogoFileName;
        settings.BusinessName = Input.BusinessName;
        settings.WarehouseName = Input.WarehouseName;
        settings.WarehouseCode = Input.WarehouseCode;
        settings.TimeZoneId = Input.TimeZoneId;
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        settings.UpdatedByUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (uploaded is not null)
        {
            settings.LogoFileName = uploaded.FileName;
            settings.LogoContentType = uploaded.ContentType;
            settings.LogoHash = uploaded.Hash;
        }
        else if (Input.RemoveLogo)
        {
            settings.LogoFileName = null;
            settings.LogoContentType = null;
            settings.LogoHash = null;
        }
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (uploaded is not null) brandingStorage.Delete(uploaded.FileName);
            throw;
        }
        if ((uploaded is not null || Input.RemoveLogo) && previousLogo != settings.LogoFileName)
            brandingStorage.Delete(previousLogo);
        TempData["Success"] = "Los datos del negocio fueron actualizados.";
        return RedirectToPage();
    }

    public sealed class InputModel
    {
        [Required, StringLength(160)] public string BusinessName { get; set; } = string.Empty;
        [Required, StringLength(120)] public string WarehouseName { get; set; } = string.Empty;
        [Required, StringLength(30), RegularExpression("^[A-Z0-9][A-Z0-9-]*$")] public string WarehouseCode { get; set; } = string.Empty;
        [Required, StringLength(100)] public string TimeZoneId { get; set; } = string.Empty;
        [DataType(DataType.Upload)] public IFormFile? Logo { get; set; }
        public bool RemoveLogo { get; set; }
    }
}
