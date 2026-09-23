using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowAnonymousPalletIdentification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<System.Guid>(
                name: "ResponsibleUserId",
                table: "pallet_plate_events",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(System.Guid),
                oldType: "uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<System.Guid>(
                name: "ResponsibleUserId",
                table: "pallet_plate_events",
                type: "uuid",
                nullable: false,
                defaultValue: System.Guid.Empty,
                oldClrType: typeof(System.Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
