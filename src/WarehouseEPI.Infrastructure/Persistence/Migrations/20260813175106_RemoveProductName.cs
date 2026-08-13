using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveProductName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM public.products) THEN
                        RAISE EXCEPTION 'RemoveProductName requires public.products to be empty.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "name",
                table: "products");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "products",
                type: "character varying(180)",
                maxLength: 180,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE public.products
                SET name = left(COALESCE(NULLIF(btrim(description), ''), sku), 180);
                """);

            migrationBuilder.AlterColumn<string>(
                name: "name",
                table: "products",
                type: "character varying(180)",
                maxLength: 180,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(180)",
                oldMaxLength: 180,
                oldNullable: true);
        }
    }
}
