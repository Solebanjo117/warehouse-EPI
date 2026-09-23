using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecipeDraftMaterialStages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_production_recipe_lines_recipe_id_material_product_id_stage~",
                table: "production_recipe_lines");

            migrationBuilder.AlterColumn<Guid>(
                name: "stage_id",
                table: "production_recipe_lines",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateIndex(
                name: "IX_production_recipe_lines_recipe_id_material_product_id",
                table: "production_recipe_lines",
                columns: new[] { "recipe_id", "material_product_id" },
                unique: true,
                filter: "stage_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_production_recipe_lines_recipe_id_material_product_id_stage~",
                table: "production_recipe_lines",
                columns: new[] { "recipe_id", "material_product_id", "stage_id" },
                unique: true,
                filter: "stage_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM production_recipe_lines WHERE stage_id IS NULL) THEN
                        RAISE EXCEPTION 'No se puede revertir RecipeDraftMaterialStages mientras existan materiales sin etapa asignada.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_production_recipe_lines_recipe_id_material_product_id",
                table: "production_recipe_lines");

            migrationBuilder.DropIndex(
                name: "IX_production_recipe_lines_recipe_id_material_product_id_stage~",
                table: "production_recipe_lines");

            migrationBuilder.AlterColumn<Guid>(
                name: "stage_id",
                table: "production_recipe_lines",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_recipe_lines_recipe_id_material_product_id_stage~",
                table: "production_recipe_lines",
                columns: new[] { "recipe_id", "material_product_id", "stage_id" },
                unique: true);
        }
    }
}
