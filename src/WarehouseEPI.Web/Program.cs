using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Imports;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Web.Bootstrap;
using WarehouseEPI.Web.Imports;
using WarehouseEPI.Web.Locations;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Inventory;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin/Users", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Admin/Catalogs", "AdminOnly");
});
builder.Services.AddDbContext<WarehouseDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Warehouse")));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IProductSpreadsheetReader, ProductSpreadsheetReader>();
builder.Services.AddSingleton<ProductImportPreviewStore>();
builder.Services.AddScoped<ProductImportService>();
builder.Services.AddSingleton<LocationGenerationPreviewStore>();
builder.Services.AddScoped<LocationGenerationService>();
builder.Services.AddScoped<LocationLookupService>();
builder.Services.AddScoped<ProductLocationAssignmentService>();
builder.Services.AddScoped<InventoryMovementService>();
builder.Services.AddScoped<InventoryQueryService>();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = ProductImportLimits.MaxRequestBytes;
    options.MemoryBufferThreshold = checked((int)ProductImportLimits.MaxRequestBytes);
});

var pinLookupKey = builder.Configuration["Security:PinLookupKey"];
if (string.IsNullOrWhiteSpace(pinLookupKey))
{
    throw new InvalidOperationException(
        "Falta Security:PinLookupKey en User Secrets.");
}

builder.Services.AddSingleton(new PinProtector(pinLookupKey));
builder.Services.AddScoped<UserPinService>();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Admin/Login";
        options.AccessDeniedPath = "/Admin/Login";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = async context =>
        {
            var userIdValue = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdValue, out var userId))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync();
                return;
            }

            var dbContext = context.HttpContext.RequestServices
                .GetRequiredService<WarehouseDbContext>();
            var user = await dbContext.Users
                .AsNoTracking()
                .Include(candidate => candidate.Role)
                .SingleOrDefaultAsync(candidate => candidate.Id == userId);

            if (user is not { IsActive: true } || user.Role.Code != "ADMIN")
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync();
            }
        };
    });
builder.Services.AddAuthorization(options =>
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireAuthenticatedUser().RequireRole("ADMIN")));

var app = builder.Build();

if (args.Contains("--create-admin", StringComparer.OrdinalIgnoreCase))
{
    await AdminBootstrapper.RunAsync(app.Services);
    return;
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();

public partial class Program;
