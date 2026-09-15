START TRANSACTION;
ALTER TABLE production_work_orders ADD supply_priority character varying(12) NOT NULL DEFAULT 'Normal';

ALTER TABLE production_work_orders ADD uses_supply_requests boolean NOT NULL DEFAULT FALSE;

ALTER TABLE production_material_issue_links ADD supply_request_line_id uuid;

CREATE TABLE production_supply_requests (
    id uuid NOT NULL,
    work_order_id uuid NOT NULL,
    work_order_stage_id uuid NOT NULL,
    destination_code character varying(80) NOT NULL,
    destination_location_id uuid,
    status character varying(20) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    version bigint NOT NULL,
    CONSTRAINT "PK_production_supply_requests" PRIMARY KEY (id),
    CONSTRAINT "FK_production_supply_requests_locations_destination_location_id" FOREIGN KEY (destination_location_id) REFERENCES locations (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_supply_requests_production_work_order_stages_wor~" FOREIGN KEY (work_order_stage_id) REFERENCES production_work_order_stages (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_supply_requests_production_work_orders_work_orde~" FOREIGN KEY (work_order_id) REFERENCES production_work_orders (id) ON DELETE RESTRICT
);

CREATE TABLE production_supply_request_lines (
    id uuid NOT NULL,
    supply_request_id uuid NOT NULL,
    material_plan_id uuid NOT NULL,
    product_id uuid NOT NULL,
    unit_id smallint NOT NULL,
    required_quantity numeric(18,4) NOT NULL,
    cancelled_quantity numeric(18,4) NOT NULL,
    CONSTRAINT "PK_production_supply_request_lines" PRIMARY KEY (id),
    CONSTRAINT ck_production_supply_request_line_quantities CHECK (required_quantity > 0 AND cancelled_quantity >= 0 AND cancelled_quantity <= required_quantity),
    CONSTRAINT "FK_production_supply_request_lines_production_order_material_p~" FOREIGN KEY (material_plan_id) REFERENCES production_order_material_plans (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_supply_request_lines_production_supply_requests_~" FOREIGN KEY (supply_request_id) REFERENCES production_supply_requests (id) ON DELETE CASCADE,
    CONSTRAINT "FK_production_supply_request_lines_products_product_id" FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_supply_request_lines_units_unit_id" FOREIGN KEY (unit_id) REFERENCES units (id) ON DELETE RESTRICT
);

CREATE TABLE production_supply_events (
    id uuid NOT NULL,
    operation_id uuid NOT NULL,
    request_fingerprint character(64) NOT NULL,
    supply_request_id uuid NOT NULL,
    supply_request_line_id uuid,
    type character varying(24) NOT NULL,
    responsible_user_id uuid NOT NULL,
    quantity numeric(18,4) NOT NULL,
    reason character varying(500),
    inventory_movement_id uuid,
    recorded_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_production_supply_events" PRIMARY KEY (id),
    CONSTRAINT "FK_production_supply_events_inventory_movements_inventory_move~" FOREIGN KEY (inventory_movement_id) REFERENCES inventory_movements (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_supply_events_production_supply_request_lines_su~" FOREIGN KEY (supply_request_line_id) REFERENCES production_supply_request_lines (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_supply_events_production_supply_requests_supply_~" FOREIGN KEY (supply_request_id) REFERENCES production_supply_requests (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_supply_events_users_responsible_user_id" FOREIGN KEY (responsible_user_id) REFERENCES users (id) ON DELETE RESTRICT
);

CREATE TABLE production_warehouse_reservations (
    id uuid NOT NULL,
    supply_request_line_id uuid NOT NULL,
    location_id uuid NOT NULL,
    lot_id uuid NOT NULL,
    quantity numeric(18,4) NOT NULL,
    released_quantity numeric(18,4) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_production_warehouse_reservations" PRIMARY KEY (id),
    CONSTRAINT ck_production_warehouse_reservation_quantities CHECK (quantity > 0 AND released_quantity >= 0 AND released_quantity <= quantity),
    CONSTRAINT "FK_production_warehouse_reservations_locations_location_id" FOREIGN KEY (location_id) REFERENCES locations (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_warehouse_reservations_product_lots_lot_id" FOREIGN KEY (lot_id) REFERENCES product_lots (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_warehouse_reservations_production_supply_request~" FOREIGN KEY (supply_request_line_id) REFERENCES production_supply_request_lines (id) ON DELETE RESTRICT
);

CREATE INDEX "IX_production_material_issue_links_supply_request_line_id" ON production_material_issue_links (supply_request_line_id);

CREATE INDEX "IX_production_supply_events_inventory_movement_id" ON production_supply_events (inventory_movement_id);

CREATE UNIQUE INDEX "IX_production_supply_events_operation_id" ON production_supply_events (operation_id);

CREATE INDEX "IX_production_supply_events_responsible_user_id" ON production_supply_events (responsible_user_id);

CREATE INDEX "IX_production_supply_events_supply_request_id_recorded_at" ON production_supply_events (supply_request_id, recorded_at);

CREATE INDEX "IX_production_supply_events_supply_request_line_id" ON production_supply_events (supply_request_line_id);

CREATE UNIQUE INDEX "IX_production_supply_request_lines_material_plan_id" ON production_supply_request_lines (material_plan_id);

CREATE INDEX "IX_production_supply_request_lines_product_id" ON production_supply_request_lines (product_id);

CREATE INDEX "IX_production_supply_request_lines_supply_request_id" ON production_supply_request_lines (supply_request_id);

CREATE INDEX "IX_production_supply_request_lines_unit_id" ON production_supply_request_lines (unit_id);

CREATE INDEX "IX_production_supply_requests_destination_location_id" ON production_supply_requests (destination_location_id);

CREATE UNIQUE INDEX "IX_production_supply_requests_work_order_id_work_order_stage_i~" ON production_supply_requests (work_order_id, work_order_stage_id, destination_code);

CREATE INDEX "IX_production_supply_requests_work_order_stage_id" ON production_supply_requests (work_order_stage_id);

CREATE INDEX "IX_production_warehouse_reservations_location_id_lot_id" ON production_warehouse_reservations (location_id, lot_id);

CREATE INDEX "IX_production_warehouse_reservations_lot_id" ON production_warehouse_reservations (lot_id);

CREATE UNIQUE INDEX "IX_production_warehouse_reservations_supply_request_line_id_lo~" ON production_warehouse_reservations (supply_request_line_id, location_id, lot_id);

ALTER TABLE production_material_issue_links ADD CONSTRAINT "FK_production_material_issue_links_production_supply_request_l~" FOREIGN KEY (supply_request_line_id) REFERENCES production_supply_request_lines (id) ON DELETE RESTRICT;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260914181154_Phase133ProductionSupplyRequests', '10.0.10');

COMMIT;

