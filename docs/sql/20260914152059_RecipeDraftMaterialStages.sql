START TRANSACTION;
DROP INDEX "IX_production_recipe_lines_recipe_id_material_product_id_stage~";

ALTER TABLE production_recipe_lines ALTER COLUMN stage_id DROP NOT NULL;

CREATE UNIQUE INDEX "IX_production_recipe_lines_recipe_id_material_product_id" ON production_recipe_lines (recipe_id, material_product_id) WHERE stage_id IS NULL;

CREATE UNIQUE INDEX "IX_production_recipe_lines_recipe_id_material_product_id_stage~" ON production_recipe_lines (recipe_id, material_product_id, stage_id) WHERE stage_id IS NOT NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260914152059_RecipeDraftMaterialStages', '10.0.10');

COMMIT;

