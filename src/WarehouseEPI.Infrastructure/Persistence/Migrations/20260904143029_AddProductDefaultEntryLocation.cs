using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductDefaultEntryLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "default_entry_location_id",
                table: "products",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_products_default_entry_location_id",
                table: "products",
                column: "default_entry_location_id");

            migrationBuilder.AddForeignKey(
                name: "FK_products_locations_default_entry_location_id",
                table: "products",
                column: "default_entry_location_id",
                principalTable: "locations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_products_locations_default_entry_location_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_default_entry_location_id",
                table: "products");

            migrationBuilder.DropColumn(
                name: "default_entry_location_id",
                table: "products");
        }
    }
}
