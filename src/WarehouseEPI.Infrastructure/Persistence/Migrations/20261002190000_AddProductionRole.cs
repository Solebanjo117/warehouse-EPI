using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

public partial class AddProductionRole : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.InsertData(
        table: "roles", columns: new[] { "id", "code", "name", "created_at" },
        values: new object[] { (short)3, "PRODUCTION", "Producción", DateTimeOffset.UnixEpoch });

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DeleteData(
        table: "roles", keyColumn: "id", keyValue: (short)3);
}
