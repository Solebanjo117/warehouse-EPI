using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using WarehouseEPI.Infrastructure.Imports;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Bootstrap;
using WarehouseEPI.Web.Branding;
using WarehouseEPI.Web.Hosting;
using WarehouseEPI.Web.Imports;
using WarehouseEPI.Web.Locations;
using WarehouseEPI.Web.Observability;
using WarehouseEPI.Web.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "WarehouseEPI");
var isIntegrationTestHost = AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
    string.Equals(assembly.GetName().Name, "Microsoft.AspNetCore.Mvc.Testing", StringComparison.Ordinal));
var isProtectedProduction = builder.Environment.IsProduction() && !isIntegrationTestHost;
if (isProtectedProduction)
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
    StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);
}
var configuredServicePath = builder.Configuration[ServiceConfigurationLoader.ConfigurationKey];
if (!string.IsNullOrWhiteSpace(configuredServicePath) && !isProtectedProduction)
    throw new InvalidOperationException("ServiceConfigPath solo puede utilizarse en Production.");
var serviceConfigurationPath = ServiceConfigurationLoader.AddIfConfigured(builder.Configuration);

var productionSecurity = isProtectedProduction
    ? ProductionSecuritySettings.Load(builder.Configuration)
    : null;
var observability = ObservabilitySettings.Load(builder.Configuration, isProtectedProduction);
if (isProtectedProduction)
{
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(new JsonRollingFileLoggerProvider(observability));
}
var rateLimits = productionSecurity?.RateLimits ?? new SecurityRateLimitSettings
{
    AdminLoginPermitLimit = 5,
    AdminLoginWindowMinutes = 5,
    AdminPostPermitLimit = 10,
    AdminPostWindowMinutes = 1,
    OperationPostPermitLimit = 30,
    OperationPostWindowMinutes = 1
};
rateLimits.Validate();

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = ProductImportLimits.MaxRequestBytes;
    if (productionSecurity is null)
        return;

    var certificate = productionSecurity.LoadServerCertificate();
    options.ListenAnyIP(80);
    options.ListenAnyIP(443, listen => listen.UseHttps(certificate, https =>
        https.SslProtocols = ProductionSecuritySettings.SupportedTlsProtocols));
});

// Add services to the container.
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin/Users", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Admin/Catalogs", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Admin/Inventory", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Admin/System", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Admin/Account", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Admin/Settings", "AdminOnly");
});
builder.Services.AddDbContext<WarehouseDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Warehouse")));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new ApplicationLifetimeInfo(TimeProvider.System));
builder.Services.AddSingleton<RecentFailureStore>();
builder.Services.AddScoped<SystemStatusService>();
builder.Services.AddHealthChecks()
    .AddCheck("process", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<DatabaseHealthCheck>("postgresql", tags: ["database"]);
builder.Services.AddSingleton<IProductSpreadsheetReader, ProductSpreadsheetReader>();
builder.Services.AddSingleton<ProductImportPreviewStore>();
builder.Services.AddScoped<ProductImportService>();
builder.Services.AddSingleton<LocationGenerationPreviewStore>();
builder.Services.AddScoped<LocationGenerationService>();
builder.Services.AddScoped<LocationLookupService>();
builder.Services.AddScoped<ProductLocationAssignmentService>();
builder.Services.AddScoped<InventoryMovementService>();
builder.Services.AddScoped<InventoryCorrectionService>();
builder.Services.AddScoped<InventoryHistoryService>();
builder.Services.AddScoped<ProductLotAdministrationService>();
builder.Services.AddScoped<InventoryQueryService>();
builder.Services.AddScoped<OperationalInventoryQueryService>();
builder.Services.AddScoped<WarehouseSettingsService>();
builder.Services.AddScoped<WarehouseClock>();
builder.Services.AddSingleton<BrandingStorage>();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = ProductImportLimits.MaxRequestBytes;
    options.MemoryBufferThreshold = checked((int)ProductImportLimits.MaxRequestBytes);
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString();
        await context.HttpContext.Response.WriteAsync(
            "Demasiadas solicitudes. Espere antes de volver a intentarlo.", cancellationToken);
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (!HttpMethods.IsPost(context.Request.Method))
            return RateLimitPartition.GetNoLimiter("read");

        var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (context.Request.Path.Equals("/Admin/Login", StringComparison.OrdinalIgnoreCase))
            return FixedWindowPartition($"admin-login:{remoteAddress}",
                rateLimits.AdminLoginPermitLimit, TimeSpan.FromMinutes(rateLimits.AdminLoginWindowMinutes));
        if (context.Request.Path.StartsWithSegments("/Operations"))
            return FixedWindowPartition($"operations:{remoteAddress}",
                rateLimits.OperationPostPermitLimit, TimeSpan.FromMinutes(rateLimits.OperationPostWindowMinutes));
        if (context.Request.Path.StartsWithSegments("/Admin"))
            return FixedWindowPartition($"admin:{remoteAddress}",
                rateLimits.AdminPostPermitLimit, TimeSpan.FromMinutes(rateLimits.AdminPostWindowMinutes));

        return RateLimitPartition.GetNoLimiter("other");
    });
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
        options.Cookie.Name = productionSecurity is null ? "WarehouseEPI.Admin" : "__Host-WarehouseEPI.Admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = productionSecurity is null
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
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
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = productionSecurity is null ? "WarehouseEPI.Antiforgery" : "__Host-WarehouseEPI.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = productionSecurity is null
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});
if (productionSecurity is not null)
{
    if (!OperatingSystem.IsWindows())
        throw new InvalidOperationException("La configuración de producción de Warehouse EPI requiere Windows y DPAPI.");
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(productionSecurity.DataProtectionKeysPath))
        .ProtectKeysWithDpapi(protectToLocalMachine: true)
        .SetApplicationName("WarehouseEPI");
}
else if (builder.Environment.IsDevelopment() &&
    builder.Configuration.GetValue<bool>("Development:UseEphemeralDataProtection"))
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();

var app = builder.Build();

if (args.Contains("--validate-production", StringComparer.OrdinalIgnoreCase))
{
    if (!isProtectedProduction || productionSecurity is null)
        throw new InvalidOperationException("--validate-production requiere ASPNETCORE_ENVIRONMENT=Production.");
    await ProductionPreflightValidator.ValidateAsync(app.Services, productionSecurity, observability);
    return;
}

if (args.Contains("--create-admin", StringComparer.OrdinalIgnoreCase))
{
    await AdminBootstrapper.RunAsync(app.Services);
    return;
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.UseHttpsRedirection();

app.UseRouting();

app.UseMiddleware<CorrelationAndRequestLoggingMiddleware>();

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        var contentSecurityPolicy = "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'";
        if (context.Request.IsHttps)
            contentSecurityPolicy += "; upgrade-insecure-requests";

        headers["Content-Security-Policy"] = contentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Frame-Options"] = "DENY";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=(), usb=(), payment=()";
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
            headers.CacheControl = "no-store";
        return Task.CompletedTask;
    });
    await next();
});

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", async Task<IResult>
    (HttpContext context, HealthCheckService healthChecks, CancellationToken cancellationToken) =>
{
    if (!LoopbackHealthEndpoint.IsLoopback(context.Connection.RemoteIpAddress))
        return TypedResults.NotFound();

    var report = await healthChecks.CheckHealthAsync(check => check.Tags.Contains("live"), cancellationToken);
    return report.Status == HealthStatus.Healthy ? TypedResults.Ok() : TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

app.MapGet("/branding/logo", async Task<IResult> (HttpContext context, WarehouseSettingsService settings, BrandingStorage storage, CancellationToken cancellationToken) =>
{
    var business = await settings.GetAsync(cancellationToken);
    var path = storage.GetPath(business.LogoFileName);
    if (path is null || business.LogoContentType is null || business.LogoHash is null)
        return TypedResults.NotFound();
    context.Response.Headers.ETag = $"\"{business.LogoHash}\"";
    context.Response.Headers.CacheControl = "public,max-age=604800,immutable";
    return Results.File(path, business.LogoContentType, enableRangeProcessing: false);
}).AllowAnonymous();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();

static RateLimitPartition<string> FixedWindowPartition(string key, int permitLimit, TimeSpan window) =>
    RateLimitPartition.GetFixedWindowLimiter(key, _ => new()
    {
        PermitLimit = permitLimit,
        Window = window,
        QueueLimit = 0,
        AutoReplenishment = true
    });

public partial class Program;
