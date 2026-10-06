using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationRackFormats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "location_rack_formats",
                columns: table => new
                {
                    row_code = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    rack_number = table.Column<short>(type: "smallint", nullable: false),
                    columns = table.Column<int>(type: "integer", nullable: false),
                    levels = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_location_rack_formats", x => new { x.row_code, x.rack_number });
                    table.CheckConstraint("ck_location_rack_formats_dimensions", "columns BETWEEN 1 AND 3 AND levels BETWEEN 1 AND 3");
                    table.CheckConstraint("ck_location_rack_formats_identity", "row_code ~ '^[A-Z]$' AND rack_number > 0");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "location_rack_formats");
        }
    }
}
