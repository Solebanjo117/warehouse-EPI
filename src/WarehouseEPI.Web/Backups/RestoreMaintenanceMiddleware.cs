namespace WarehouseEPI.Web.Backups;

public sealed class RestoreMaintenanceMiddleware(RequestDelegate next)
{
    public static bool IsPublicProgress(PathString path) => path.StartsWithSegments("/BackupRestoreProgress");
    public static bool BypassesAuthentication(PathString path) => IsPublicProgress(path) ||
        path.StartsWithSegments("/css") || path.StartsWithSegments("/js") || path.StartsWithSegments("/lib") || path.StartsWithSegments("/health");

    public async Task InvokeAsync(HttpContext context, RestoreJobStore store)
    {
        if (store.IsMaintenance && !IsPublicProgress(context.Request.Path) &&
            !context.Request.Path.StartsWithSegments("/health") &&
            !context.Request.Path.StartsWithSegments("/css") && !context.Request.Path.StartsWithSegments("/js") &&
            !context.Request.Path.StartsWithSegments("/lib"))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.RetryAfter = "30";
            if (HttpMethods.IsGet(context.Request.Method))
                context.Response.Redirect("/BackupRestoreProgress" + (store.ActiveId is { } id ? "?id=" + id : ""));
            else
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Warehouse EPI: restauración en curso. /BackupRestoreProgress", context.RequestAborted);
            }
            return;
        }
        await next(context);
    }
}
