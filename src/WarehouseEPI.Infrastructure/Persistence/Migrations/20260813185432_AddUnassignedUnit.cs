using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUnassignedUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "units",
                columns: new[] { "id", "allows_decimals", "code", "is_active", "name" },
                values: new object[] { (short)18, true, "UNASSIGNED", true, "Sin asignar" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)18);
        }
    }
}
