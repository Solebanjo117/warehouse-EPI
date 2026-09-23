START TRANSACTION;
ALTER TABLE production_supply_request_lines ADD destination_code character varying(80);

ALTER TABLE production_supply_request_lines ADD destination_location_id uuid;

ALTER TABLE production_supply_request_lines ADD reopened_quantity numeric(18,4) NOT NULL DEFAULT 0.0;

ALTER TABLE production_material_operations ADD return_effect character varying(20);

ALTER TABLE production_material_issue_links ALTER COLUMN inventory_movement_line_id DROP NOT NULL;

ALTER TABLE production_material_issue_links ADD cancelled_quantity numeric(18,4) NOT NULL DEFAULT 0.0;

ALTER TABLE production_material_issue_links ADD product_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';

ALTER TABLE production_material_issue_links ADD quantity numeric(18,4) NOT NULL DEFAULT 0.0;

ALTER TABLE production_material_issue_links ADD source character varying(20) NOT NULL DEFAULT '';

ALTER TABLE production_material_issue_links ADD wip_location_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';

UPDATE production_material_issue_links AS issue
SET product_id = line.product_id,
    wip_location_id = COALESCE(line.destination_location_id, issue.wip_location_id),
    quantity = line.quantity,
    source = 'Transfer'
FROM inventory_movement_lines AS line
WHERE issue.inventory_movement_line_id = line.id;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM production_material_issue_links
        WHERE inventory_movement_line_id IS NULL
           OR product_id = '00000000-0000-0000-0000-000000000000'
           OR wip_location_id = '00000000-0000-0000-0000-000000000000'
           OR quantity <= 0
           OR source <> 'Transfer') THEN
        RAISE EXCEPTION 'P4 cannot backfill a historical production material issue from its movement line.';
    END IF;
END $$;

CREATE TABLE production_material_issue_lots (
    id uuid NOT NULL,
    issue_link_id uuid NOT NULL,
    lot_id uuid NOT NULL,
    quantity numeric(18,4) NOT NULL,
    CONSTRAINT "PK_production_material_issue_lots" PRIMARY KEY (id),
    CONSTRAINT ck_production_material_issue_lot_quantity CHECK (quantity > 0),
    CONSTRAINT "FK_production_material_issue_lots_product_lots_lot_id" FOREIGN KEY (lot_id) REFERENCES product_lots (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_material_issue_lots_production_material_issue_li~" FOREIGN KEY (issue_link_id) REFERENCES production_material_issue_links (id) ON DELETE CASCADE
);

CREATE TABLE production_supply_preparations (
    id uuid NOT NULL,
    operation_id uuid NOT NULL,
    request_fingerprint character(64) NOT NULL,
    supply_request_line_id uuid NOT NULL,
    destination_location_id uuid NOT NULL,
    status character varying(20) NOT NULL,
    responsible_user_id uuid NOT NULL,
    version bigint NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_production_supply_preparations" PRIMARY KEY (id),
    CONSTRAINT "FK_production_supply_preparations_locations_destination_locati~" FOREIGN KEY (destination_location_id) REFERENCES locations (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_supply_preparations_production_supply_request_li~" FOREIGN KEY (supply_request_line_id) REFERENCES production_supply_request_lines (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_supply_preparations_users_responsible_user_id" FOREIGN KEY (responsible_user_id) REFERENCES users (id) ON DELETE RESTRICT
);

CREATE TABLE production_supply_confirmations (
    id uuid NOT NULL,
    operation_id uuid NOT NULL,
    request_fingerprint character(64) NOT NULL,
    supply_request_line_id uuid NOT NULL,
    preparation_id uuid NOT NULL,
    destination_location_id uuid NOT NULL,
    quantity numeric(18,4) NOT NULL,
    responsible_user_id uuid NOT NULL,
    recorded_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_production_supply_confirmations" PRIMARY KEY (id),
    CONSTRAINT ck_production_supply_confirmation_quantity CHECK (quantity > 0),
    CONSTRAINT "FK_production_supply_confirmations_locations_destination_locat~" FOREIGN KEY (destination_location_id) REFERENCES locations (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_supply_confirmations_production_supply_preparati~" FOREIGN KEY (preparation_id) REFERENCES production_supply_preparations (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_supply_confirmations_production_supply_request_l~" FOREIGN KEY (supply_request_line_id) REFERENCES production_supply_request_lines (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_supply_confirmations_users_responsible_user_id" FOREIGN KEY (responsible_user_id) REFERENCES users (id) ON DELETE RESTRICT
);

INSERT INTO production_material_issue_lots (id, issue_link_id, lot_id, quantity)
SELECT md5(issue.id::text || ':' || change.lot_id::text)::uuid,
       issue.id,
       change.lot_id,
       SUM(change.delta_quantity)
FROM production_material_issue_links AS issue
JOIN inventory_balance_changes AS change
  ON change.movement_line_id = issue.inventory_movement_line_id
 AND change.location_id = issue.wip_location_id
 AND change.lot_id IS NOT NULL
 AND change.delta_quantity > 0
GROUP BY issue.id, change.lot_id;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM production_material_issue_links AS issue
        LEFT JOIN production_material_issue_lots AS lot ON lot.issue_link_id = issue.id
        GROUP BY issue.id, issue.quantity
        HAVING COALESCE(SUM(lot.quantity), 0) <> issue.quantity) THEN
        RAISE EXCEPTION 'P4 cannot reconcile historical production material issue lots with the confirmed movement.';
    END IF;
END $$;

CREATE TABLE production_supply_preparation_sources (
    id uuid NOT NULL,
    preparation_id uuid NOT NULL,
    kind character varying(20) NOT NULL,
    location_id uuid NOT NULL,
    quantity numeric(18,4) NOT NULL,
    CONSTRAINT "PK_production_supply_preparation_sources" PRIMARY KEY (id),
    CONSTRAINT ck_production_supply_preparation_source_quantity CHECK (quantity > 0),
    CONSTRAINT "FK_production_supply_preparation_sources_locations_location_id" FOREIGN KEY (location_id) REFERENCES locations (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_supply_preparation_sources_production_supply_pre~" FOREIGN KEY (preparation_id) REFERENCES production_supply_preparations (id) ON DELETE CASCADE
);

CREATE TABLE production_supply_confirmation_issues (
    confirmation_id uuid NOT NULL,
    issue_link_id uuid NOT NULL,
    CONSTRAINT "PK_production_supply_confirmation_issues" PRIMARY KEY (confirmation_id, issue_link_id),
    CONSTRAINT "FK_production_supply_confirmation_issues_production_material_i~" FOREIGN KEY (issue_link_id) REFERENCES production_material_issue_links (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_supply_confirmation_issues_production_supply_con~" FOREIGN KEY (confirmation_id) REFERENCES production_supply_confirmations (id) ON DELETE CASCADE
);

CREATE TABLE production_supply_confirmation_movements (
    confirmation_id uuid NOT NULL,
    inventory_movement_id uuid NOT NULL,
    CONSTRAINT "PK_production_supply_confirmation_movements" PRIMARY KEY (confirmation_id, inventory_movement_id),
    CONSTRAINT "FK_production_supply_confirmation_movements_inventory_movement~" FOREIGN KEY (inventory_movement_id) REFERENCES inventory_movements (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_production_supply_confirmation_movements_production_supply_~" FOREIGN KEY (confirmation_id) REFERENCES production_supply_confirmations (id) ON DELETE CASCADE
);

CREATE INDEX "IX_production_supply_request_lines_destination_location_id" ON production_supply_request_lines (destination_location_id);

CREATE INDEX "IX_production_material_issue_links_product_id" ON production_material_issue_links (product_id);

CREATE INDEX "IX_production_material_issue_links_wip_location_id" ON production_material_issue_links (wip_location_id);

ALTER TABLE production_material_issue_links ADD CONSTRAINT ck_production_material_issue_link_cancelled CHECK (cancelled_quantity >= 0 AND cancelled_quantity <= quantity);

CREATE UNIQUE INDEX "IX_production_material_issue_lots_issue_link_id_lot_id" ON production_material_issue_lots (issue_link_id, lot_id);

CREATE INDEX "IX_production_material_issue_lots_lot_id" ON production_material_issue_lots (lot_id);

CREATE INDEX "IX_production_supply_confirmation_issues_issue_link_id" ON production_supply_confirmation_issues (issue_link_id);

CREATE INDEX "IX_production_supply_confirmation_movements_inventory_movement~" ON production_supply_confirmation_movements (inventory_movement_id);

CREATE INDEX "IX_production_supply_confirmations_destination_location_id" ON production_supply_confirmations (destination_location_id);

CREATE UNIQUE INDEX "IX_production_supply_confirmations_operation_id" ON production_supply_confirmations (operation_id);

CREATE UNIQUE INDEX "IX_production_supply_confirmations_preparation_id" ON production_supply_confirmations (preparation_id);

CREATE INDEX "IX_production_supply_confirmations_responsible_user_id" ON production_supply_confirmations (responsible_user_id);

CREATE INDEX "IX_production_supply_confirmations_supply_request_line_id" ON production_supply_confirmations (supply_request_line_id);

CREATE INDEX "IX_production_supply_preparation_sources_location_id" ON production_supply_preparation_sources (location_id);

CREATE UNIQUE INDEX "IX_production_supply_preparation_sources_preparation_id_kind_l~" ON production_supply_preparation_sources (preparation_id, kind, location_id);

CREATE INDEX "IX_production_supply_preparations_destination_location_id" ON production_supply_preparations (destination_location_id);

CREATE UNIQUE INDEX "IX_production_supply_preparations_operation_id" ON production_supply_preparations (operation_id);

CREATE INDEX "IX_production_supply_preparations_responsible_user_id" ON production_supply_preparations (responsible_user_id);

CREATE UNIQUE INDEX "IX_production_supply_preparations_supply_request_line_id" ON production_supply_preparations (supply_request_line_id) WHERE status = 'Open';

ALTER TABLE production_material_issue_links ADD CONSTRAINT "FK_production_material_issue_links_locations_wip_location_id" FOREIGN KEY (wip_location_id) REFERENCES locations (id) ON DELETE RESTRICT;

ALTER TABLE production_material_issue_links ADD CONSTRAINT "FK_production_material_issue_links_products_product_id" FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT;

ALTER TABLE production_supply_request_lines ADD CONSTRAINT "FK_production_supply_request_lines_locations_destination_locat~" FOREIGN KEY (destination_location_id) REFERENCES locations (id) ON DELETE RESTRICT;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260915143955_Phase134GuidedProductionSupply', '10.0.10');

COMMIT;

