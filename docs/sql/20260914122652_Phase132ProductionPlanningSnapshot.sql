START TRANSACTION;
ALTER TABLE production_order_material_plans ADD wip_location_id uuid;

ALTER TABLE production_order_material_plans ADD wip_rack_number smallint;

ALTER TABLE production_order_material_plans ADD wip_resolution_source character varying(30);

ALTER TABLE production_order_material_plans ADD wip_row_code character varying(20);

ALTER TABLE production_order_material_plans ADD wip_target_code character varying(80);

ALTER TABLE production_order_material_plans ADD wip_target_kind character varying(20);

CREATE TABLE production_order_planning_revisions (
    id uuid NOT NULL,
    operation_id uuid NOT NULL,
    request_fingerprint character(64) NOT NULL,
    work_order_id uuid NOT NULL,
    authorized_by_user_id uuid NOT NULL,
    reason character varying(500) NOT NULL,
    before_json text NOT NULL,
    after_json text NOT NULL,
    recorded_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_production_order_planning_revisions" PRIMARY KEY (id),
    CONSTRAINT "FK_production_order_planning_revisions_production_work_orders_~" FOREIGN KEY (work_order_id) REFERENCES production_work_orders (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_order_planning_revisions_users_authorized_by_use~" FOREIGN KEY (authorized_by_user_id) REFERENCES users (id) ON DELETE RESTRICT
);

CREATE INDEX "IX_production_order_material_plans_wip_location_id" ON production_order_material_plans (wip_location_id);

ALTER TABLE production_order_material_plans ADD CONSTRAINT ck_production_order_material_plan_wip_shape CHECK ((wip_target_kind IS NULL AND wip_location_id IS NULL AND wip_row_code IS NULL AND wip_rack_number IS NULL AND wip_target_code IS NULL AND wip_resolution_source IS NULL) OR (wip_target_kind IN ('Area', 'Position') AND wip_location_id IS NOT NULL AND wip_row_code IS NULL AND wip_rack_number IS NULL AND wip_target_code IS NOT NULL AND wip_resolution_source IS NOT NULL) OR (wip_target_kind = 'Rack' AND wip_location_id IS NULL AND wip_row_code IS NOT NULL AND wip_rack_number IS NOT NULL AND wip_target_code IS NOT NULL AND wip_resolution_source IS NOT NULL));

CREATE INDEX "IX_production_order_planning_revisions_authorized_by_user_id" ON production_order_planning_revisions (authorized_by_user_id);

CREATE UNIQUE INDEX "IX_production_order_planning_revisions_operation_id" ON production_order_planning_revisions (operation_id);

CREATE INDEX "IX_production_order_planning_revisions_work_order_id_recorded_~" ON production_order_planning_revisions (work_order_id, recorded_at);

ALTER TABLE production_order_material_plans ADD CONSTRAINT "FK_production_order_material_plans_locations_wip_location_id" FOREIGN KEY (wip_location_id) REFERENCES locations (id) ON DELETE RESTRICT;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260914122652_Phase132ProductionPlanningSnapshot', '10.0.10');

COMMIT;

