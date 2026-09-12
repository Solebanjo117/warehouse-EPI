START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260911184026_MaterialWipDefaults') THEN
    ALTER TABLE production_stages ADD default_wip_location_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260911184026_MaterialWipDefaults') THEN
    ALTER TABLE production_stages ADD default_wip_rack_number smallint;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260911184026_MaterialWipDefaults') THEN
    ALTER TABLE production_stages ADD default_wip_row_code character varying(20);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260911184026_MaterialWipDefaults') THEN
    CREATE TABLE production_material_wip_defaults (
        id uuid NOT NULL,
        product_id uuid NOT NULL,
        production_stage_id uuid NOT NULL,
        location_id uuid,
        row_code character varying(20),
        rack_number smallint,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_production_material_wip_defaults" PRIMARY KEY (id),
        CONSTRAINT ck_production_material_wip_defaults_shape CHECK ((location_id IS NOT NULL AND row_code IS NULL AND rack_number IS NULL) OR (location_id IS NULL AND row_code IS NOT NULL AND rack_number IS NOT NULL)),
        CONSTRAINT "FK_production_material_wip_defaults_locations_location_id" FOREIGN KEY (location_id) REFERENCES locations (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_production_material_wip_defaults_production_stages_producti~" FOREIGN KEY (production_stage_id) REFERENCES production_stages (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_production_material_wip_defaults_products_product_id" FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260911184026_MaterialWipDefaults') THEN
    CREATE TABLE production_material_wip_revisions (
        id uuid NOT NULL,
        operation_id uuid NOT NULL,
        request_fingerprint character(64) NOT NULL,
        product_id uuid NOT NULL,
        authorized_by_user_id uuid NOT NULL,
        reason character varying(500) NOT NULL,
        before_json jsonb NOT NULL,
        after_json jsonb NOT NULL,
        recorded_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_production_material_wip_revisions" PRIMARY KEY (id),
        CONSTRAINT "FK_production_material_wip_revisions_products_product_id" FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_production_material_wip_revisions_users_authorized_by_user_~" FOREIGN KEY (authorized_by_user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260911184026_MaterialWipDefaults') THEN
    CREATE INDEX "IX_production_stages_default_wip_location_id" ON production_stages (default_wip_location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260911184026_MaterialWipDefaults') THEN
    ALTER TABLE production_stages ADD CONSTRAINT ck_production_stages_default_wip_shape CHECK ((default_wip_location_id IS NULL AND default_wip_row_code IS NULL AND default_wip_rack_number IS NULL) OR (default_wip_location_id IS NOT NULL AND default_wip_row_code IS NULL AND default_wip_rack_number IS NULL) OR (default_wip_location_id IS NULL AND default_wip_row_code IS NOT NULL AND default_wip_rack_number IS NOT NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260911184026_MaterialWipDefaults') THEN
    CREATE INDEX "IX_production_material_wip_defaults_location_id" ON production_material_wip_defaults (location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260911184026_MaterialWipDefaults') THEN
    CREATE UNIQUE INDEX "IX_production_material_wip_defaults_product_id_production_stag~" ON production_material_wip_defaults (product_id, production_stage_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260911184026_MaterialWipDefaults') THEN
    CREATE INDEX "IX_production_material_wip_defaults_production_stage_id" ON production_material_wip_defaults (production_stage_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260911184026_MaterialWipDefaults') THEN
    CREATE INDEX "IX_production_material_wip_revisions_authorized_by_user_id" ON production_material_wip_revisions (authorized_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260911184026_MaterialWipDefaults') THEN
    CREATE UNIQUE INDEX "IX_production_material_wip_revisions_operation_id" ON production_material_wip_revisions (operation_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260911184026_MaterialWipDefaults') THEN
    CREATE INDEX "IX_production_material_wip_revisions_product_id" ON production_material_wip_revisions (product_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260911184026_MaterialWipDefaults') THEN
    ALTER TABLE production_stages ADD CONSTRAINT "FK_production_stages_locations_default_wip_location_id" FOREIGN KEY (default_wip_location_id) REFERENCES locations (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260911184026_MaterialWipDefaults') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260911184026_MaterialWipDefaults', '10.0.10');
    END IF;
END $EF$;
COMMIT;

