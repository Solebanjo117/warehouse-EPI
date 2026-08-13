using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Users;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(WarehouseDbContext dbContext) : PageModel
{
    public IReadOnlyList<UserRow> Users { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Users = await dbContext.Users
            .AsNoTracking()
            .OrderBy(user => user.FullName)
            .Select(user => new UserRow(
                user.Id,
                user.FullName,
                user.Role.Code,
                user.Role.Name,
                user.IsActive))
            .ToListAsync(cancellationToken);
    }

    public sealed record UserRow(
        Guid Id,
        string FullName,
        string RoleCode,
        string RoleName,
        bool IsActive);
}
