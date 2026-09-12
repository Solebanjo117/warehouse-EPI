START TRANSACTION;
ALTER TABLE production_work_orders ADD recipe_version integer;

ALTER TABLE production_work_orders ADD uses_batch_traceability boolean NOT NULL DEFAULT FALSE;

ALTER TABLE production_events ADD batch_id uuid;

ALTER TABLE production_events ADD related_event_id uuid;

CREATE TABLE production_batches (
    id uuid NOT NULL,
    create_operation_id uuid NOT NULL,
    create_fingerprint character(64) NOT NULL,
    work_order_id uuid NOT NULL,
    number character varying(60) NOT NULL,
    assigned_quantity numeric(18,4) NOT NULL,
    finished_product_lot_id uuid NOT NULL,
    created_by_user_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    version bigint NOT NULL,
    CONSTRAINT "PK_production_batches" PRIMARY KEY (id),
    CONSTRAINT ck_production_batch_quantity CHECK (assigned_quantity > 0),
    CONSTRAINT "FK_production_batches_product_lots_finished_product_lot_id" FOREIGN KEY (finished_product_lot_id) REFERENCES product_lots (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_batches_production_work_orders_work_order_id" FOREIGN KEY (work_order_id) REFERENCES production_work_orders (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_batches_users_created_by_user_id" FOREIGN KEY (created_by_user_id) REFERENCES users (id) ON DELETE RESTRICT
);

CREATE TABLE production_order_material_plans (
    id uuid NOT NULL,
    work_order_id uuid NOT NULL,
    work_order_stage_id uuid NOT NULL,
    material_product_id uuid NOT NULL,
    unit_id smallint NOT NULL,
    planned_quantity numeric(18,4) NOT NULL,
    original_planned_quantity numeric(18,4) NOT NULL,
    adjustment_reason character varying(500),
    adjusted_by_user_id uuid,
    adjusted_at timestamp with time zone,
    CONSTRAINT "PK_production_order_material_plans" PRIMARY KEY (id),
    CONSTRAINT ck_production_order_material_plan_quantities CHECK (planned_quantity > 0 AND original_planned_quantity > 0),
    CONSTRAINT "FK_production_order_material_plans_production_work_order_stage~" FOREIGN KEY (work_order_stage_id) REFERENCES production_work_order_stages (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_order_material_plans_production_work_orders_work~" FOREIGN KEY (work_order_id) REFERENCES production_work_orders (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_order_material_plans_products_material_product_id" FOREIGN KEY (material_product_id) REFERENCES products (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_order_material_plans_units_unit_id" FOREIGN KEY (unit_id) REFERENCES units (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_order_material_plans_users_adjusted_by_user_id" FOREIGN KEY (adjusted_by_user_id) REFERENCES users (id) ON DELETE RESTRICT
);

CREATE TABLE production_recipes (
    id uuid NOT NULL,
    product_id uuid NOT NULL,
    version integer NOT NULL,
    base_quantity numeric(18,4) NOT NULL,
    is_active boolean NOT NULL,
    reason character varying(500) NOT NULL,
    created_by_user_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_production_recipes" PRIMARY KEY (id),
    CONSTRAINT ck_production_recipe_base_quantity CHECK (base_quantity > 0),
    CONSTRAINT "FK_production_recipes_products_product_id" FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_recipes_users_created_by_user_id" FOREIGN KEY (created_by_user_id) REFERENCES users (id) ON DELETE RESTRICT
);

CREATE TABLE production_batch_results (
    id uuid NOT NULL,
    operation_id uuid NOT NULL,
    request_fingerprint character(64) NOT NULL,
    batch_id uuid NOT NULL,
    work_order_stage_id uuid NOT NULL,
    shift_id uuid NOT NULL,
    responsible_user_id uuid NOT NULL,
    is_rework boolean NOT NULL,
    input_quantity numeric(18,4) NOT NULL,
    good_quantity numeric(18,4) NOT NULL,
    rework_quantity numeric(18,4) NOT NULL,
    scrap_quantity numeric(18,4) NOT NULL,
    difference_reason character varying(500),
    recorded_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_production_batch_results" PRIMARY KEY (id),
    CONSTRAINT ck_production_batch_result_quantities CHECK (input_quantity > 0 AND good_quantity >= 0 AND rework_quantity >= 0 AND scrap_quantity >= 0 AND good_quantity + rework_quantity + scrap_quantity = input_quantity),
    CONSTRAINT "FK_production_batch_results_production_batches_batch_id" FOREIGN KEY (batch_id) REFERENCES production_batches (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_batch_results_production_shifts_shift_id" FOREIGN KEY (shift_id) REFERENCES production_shifts (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_batch_results_production_work_order_stages_work_~" FOREIGN KEY (work_order_stage_id) REFERENCES production_work_order_stages (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_batch_results_users_responsible_user_id" FOREIGN KEY (responsible_user_id) REFERENCES users (id) ON DELETE RESTRICT
);

CREATE TABLE production_recipe_lines (
    id uuid NOT NULL,
    recipe_id uuid NOT NULL,
    material_product_id uuid NOT NULL,
    stage_id uuid NOT NULL,
    quantity numeric(18,4) NOT NULL,
    CONSTRAINT "PK_production_recipe_lines" PRIMARY KEY (id),
    CONSTRAINT ck_production_recipe_line_quantity CHECK (quantity > 0),
    CONSTRAINT "FK_production_recipe_lines_production_recipes_recipe_id" FOREIGN KEY (recipe_id) REFERENCES production_recipes (id) ON DELETE CASCADE,
    CONSTRAINT "FK_production_recipe_lines_production_stages_stage_id" FOREIGN KEY (stage_id) REFERENCES production_stages (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_recipe_lines_products_material_product_id" FOREIGN KEY (material_product_id) REFERENCES products (id) ON DELETE RESTRICT
);

CREATE TABLE production_batch_material_consumptions (
    id uuid NOT NULL,
    batch_result_id uuid NOT NULL,
    issue_link_id uuid NOT NULL,
    material_lot_id uuid NOT NULL,
    quantity numeric(18,4) NOT NULL,
    CONSTRAINT "PK_production_batch_material_consumptions" PRIMARY KEY (id),
    CONSTRAINT ck_production_batch_material_consumption_quantity CHECK (quantity > 0),
    CONSTRAINT "FK_production_batch_material_consumptions_product_lots_materia~" FOREIGN KEY (material_lot_id) REFERENCES product_lots (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_batch_material_consumptions_production_batch_res~" FOREIGN KEY (batch_result_id) REFERENCES production_batch_results (id) ON DELETE CASCADE,
    CONSTRAINT "FK_production_batch_material_consumptions_production_material_~" FOREIGN KEY (issue_link_id) REFERENCES production_material_issue_links (id) ON DELETE RESTRICT
);

CREATE INDEX "IX_production_events_batch_id" ON production_events (batch_id);

CREATE INDEX "IX_production_events_related_event_id" ON production_events (related_event_id);

CREATE UNIQUE INDEX "IX_production_batch_material_consumptions_batch_result_id_issu~" ON production_batch_material_consumptions (batch_result_id, issue_link_id, material_lot_id);

CREATE INDEX "IX_production_batch_material_consumptions_issue_link_id" ON production_batch_material_consumptions (issue_link_id);

CREATE INDEX "IX_production_batch_material_consumptions_material_lot_id" ON production_batch_material_consumptions (material_lot_id);

CREATE INDEX "IX_production_batch_results_batch_id_work_order_stage_id_recor~" ON production_batch_results (batch_id, work_order_stage_id, recorded_at);

CREATE UNIQUE INDEX "IX_production_batch_results_operation_id" ON production_batch_results (operation_id);

CREATE INDEX "IX_production_batch_results_responsible_user_id" ON production_batch_results (responsible_user_id);

CREATE INDEX "IX_production_batch_results_shift_id" ON production_batch_results (shift_id);

CREATE INDEX "IX_production_batch_results_work_order_stage_id" ON production_batch_results (work_order_stage_id);

CREATE UNIQUE INDEX "IX_production_batches_create_operation_id" ON production_batches (create_operation_id);

CREATE INDEX "IX_production_batches_created_by_user_id" ON production_batches (created_by_user_id);

CREATE INDEX "IX_production_batches_finished_product_lot_id" ON production_batches (finished_product_lot_id);

CREATE UNIQUE INDEX "IX_production_batches_number" ON production_batches (number);

CREATE UNIQUE INDEX "IX_production_batches_work_order_id_finished_product_lot_id" ON production_batches (work_order_id, finished_product_lot_id);

CREATE INDEX "IX_production_order_material_plans_adjusted_by_user_id" ON production_order_material_plans (adjusted_by_user_id);

CREATE INDEX "IX_production_order_material_plans_material_product_id" ON production_order_material_plans (material_product_id);

CREATE INDEX "IX_production_order_material_plans_unit_id" ON production_order_material_plans (unit_id);

CREATE UNIQUE INDEX "IX_production_order_material_plans_work_order_id_work_order_st~" ON production_order_material_plans (work_order_id, work_order_stage_id, material_product_id);

CREATE INDEX "IX_production_order_material_plans_work_order_stage_id" ON production_order_material_plans (work_order_stage_id);

CREATE INDEX "IX_production_recipe_lines_material_product_id" ON production_recipe_lines (material_product_id);

CREATE UNIQUE INDEX "IX_production_recipe_lines_recipe_id_material_product_id_stage~" ON production_recipe_lines (recipe_id, material_product_id, stage_id);

CREATE INDEX "IX_production_recipe_lines_stage_id" ON production_recipe_lines (stage_id);

CREATE INDEX "IX_production_recipes_created_by_user_id" ON production_recipes (created_by_user_id);

CREATE UNIQUE INDEX "IX_production_recipes_product_id" ON production_recipes (product_id) WHERE is_active;

CREATE UNIQUE INDEX "IX_production_recipes_product_id_version" ON production_recipes (product_id, version);

ALTER TABLE production_events ADD CONSTRAINT "FK_production_events_production_batches_batch_id" FOREIGN KEY (batch_id) REFERENCES production_batches (id) ON DELETE RESTRICT;

ALTER TABLE production_events ADD CONSTRAINT "FK_production_events_production_events_related_event_id" FOREIGN KEY (related_event_id) REFERENCES production_events (id) ON DELETE RESTRICT;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260911125505_ProductionBatchTraceability', '10.0.10');

COMMIT;
