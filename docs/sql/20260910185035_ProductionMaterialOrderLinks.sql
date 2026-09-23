START TRANSACTION;
CREATE TABLE production_material_issue_links (
    id uuid NOT NULL,
    work_order_id uuid NOT NULL,
    work_order_stage_id uuid NOT NULL,
    inventory_movement_line_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_production_material_issue_links" PRIMARY KEY (id),
    CONSTRAINT "FK_production_material_issue_links_inventory_movement_lines_in~" FOREIGN KEY (inventory_movement_line_id) REFERENCES inventory_movement_lines (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_material_issue_links_production_work_order_stage~" FOREIGN KEY (work_order_stage_id) REFERENCES production_work_order_stages (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_material_issue_links_production_work_orders_work~" FOREIGN KEY (work_order_id) REFERENCES production_work_orders (id) ON DELETE RESTRICT
);

CREATE TABLE production_material_operations (
    id uuid NOT NULL,
    operation_id uuid NOT NULL,
    request_fingerprint character(64) NOT NULL,
    work_order_id uuid NOT NULL,
    work_order_stage_id uuid NOT NULL,
    type character varying(24) NOT NULL,
    responsible_user_id uuid NOT NULL,
    reverses_operation_id uuid,
    reference character varying(120),
    notes character varying(500),
    recorded_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_production_material_operations" PRIMARY KEY (id),
    CONSTRAINT ck_production_material_operation_reversal CHECK ((type = 'REVERSAL' AND reverses_operation_id IS NOT NULL) OR (type <> 'REVERSAL' AND reverses_operation_id IS NULL)),
    CONSTRAINT "FK_production_material_operations_production_material_operatio~" FOREIGN KEY (reverses_operation_id) REFERENCES production_material_operations (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_material_operations_production_work_order_stages~" FOREIGN KEY (work_order_stage_id) REFERENCES production_work_order_stages (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_material_operations_production_work_orders_work_~" FOREIGN KEY (work_order_id) REFERENCES production_work_orders (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_material_operations_users_responsible_user_id" FOREIGN KEY (responsible_user_id) REFERENCES users (id) ON DELETE RESTRICT
);

CREATE TABLE production_material_operation_lines (
    id uuid NOT NULL,
    production_material_operation_id uuid NOT NULL,
    issue_link_id uuid NOT NULL,
    inventory_movement_line_id uuid NOT NULL,
    quantity numeric(18,4) NOT NULL,
    CONSTRAINT "PK_production_material_operation_lines" PRIMARY KEY (id),
    CONSTRAINT ck_production_material_operation_line_quantity CHECK (quantity > 0),
    CONSTRAINT "FK_production_material_operation_lines_inventory_movement_line~" FOREIGN KEY (inventory_movement_line_id) REFERENCES inventory_movement_lines (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_material_operation_lines_production_material_iss~" FOREIGN KEY (issue_link_id) REFERENCES production_material_issue_links (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_material_operation_lines_production_material_ope~" FOREIGN KEY (production_material_operation_id) REFERENCES production_material_operations (id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX "IX_production_material_issue_links_inventory_movement_line_id" ON production_material_issue_links (inventory_movement_line_id);

CREATE INDEX "IX_production_material_issue_links_work_order_id_work_order_st~" ON production_material_issue_links (work_order_id, work_order_stage_id);

CREATE INDEX "IX_production_material_issue_links_work_order_stage_id" ON production_material_issue_links (work_order_stage_id);

CREATE UNIQUE INDEX "IX_production_material_operation_lines_inventory_movement_line~" ON production_material_operation_lines (inventory_movement_line_id);

CREATE INDEX "IX_production_material_operation_lines_issue_link_id" ON production_material_operation_lines (issue_link_id);

CREATE UNIQUE INDEX "IX_production_material_operation_lines_production_material_ope~" ON production_material_operation_lines (production_material_operation_id, issue_link_id);

CREATE UNIQUE INDEX "IX_production_material_operations_operation_id" ON production_material_operations (operation_id);

CREATE INDEX "IX_production_material_operations_responsible_user_id" ON production_material_operations (responsible_user_id);

CREATE UNIQUE INDEX "IX_production_material_operations_reverses_operation_id" ON production_material_operations (reverses_operation_id) WHERE reverses_operation_id IS NOT NULL;

CREATE INDEX "IX_production_material_operations_work_order_id_work_order_sta~" ON production_material_operations (work_order_id, work_order_stage_id);

CREATE INDEX "IX_production_material_operations_work_order_stage_id" ON production_material_operations (work_order_stage_id);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260910185035_ProductionMaterialOrderLinks', '10.0.10');

COMMIT;

