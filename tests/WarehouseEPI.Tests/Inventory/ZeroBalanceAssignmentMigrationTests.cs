using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using WarehouseEPI.Infrastructure.Persistence.Migrations;

namespace WarehouseEPI.Tests.Inventory;

public sealed class ZeroBalanceAssignmentMigrationTests
{
    [Fact]
    public void Migration_deactivates_net_zero_assignments_and_clears_ambiguous_defaults()
    {
        var migration = new ReconcileZeroBalanceAssignments();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(ReconcileZeroBalanceAssignments)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        var sql = Assert.Single(builder.Operations.OfType<SqlOperation>()).Sql;
        Assert.Contains("HAVING COALESCE(SUM(balance.quantity), 0) = 0", sql, StringComparison.Ordinal);
        Assert.Contains("SET default_entry_location_id = NULL", sql, StringComparison.Ordinal);
        Assert.Contains("SET is_active = FALSE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("quantity < 0", sql, StringComparison.OrdinalIgnoreCase);
    }
}
