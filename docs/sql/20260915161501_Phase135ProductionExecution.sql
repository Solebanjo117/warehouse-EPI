START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    DROP INDEX "IX_production_supply_request_lines_material_plan_id";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    ALTER TABLE production_work_orders ADD original_target_quantity numeric(18,4) NOT NULL DEFAULT 0.0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    ALTER TABLE production_work_orders ADD principal_closed_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    ALTER TABLE production_supply_request_lines ADD rework_case_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE TABLE production_execution_audits (
        "Id" uuid NOT NULL,
        "OperationId" uuid NOT NULL,
        "Fingerprint" character varying(64) NOT NULL,
        "WorkOrderId" uuid,
        "Action" character varying(40) NOT NULL,
        "ResponsibleUserId" uuid NOT NULL,
        "AuthorizedByUserId" uuid,
        "Reason" character varying(500) NOT NULL,
        "BeforeJson" jsonb NOT NULL,
        "AfterJson" jsonb NOT NULL,
        "RecordedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_production_execution_audits" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_production_execution_audits_production_work_orders_WorkOrde~" FOREIGN KEY ("WorkOrderId") REFERENCES production_work_orders (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_production_execution_audits_users_AuthorizedByUserId" FOREIGN KEY ("AuthorizedByUserId") REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_production_execution_audits_users_ResponsibleUserId" FOREIGN KEY ("ResponsibleUserId") REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE TABLE production_reasons (
        "Id" uuid NOT NULL,
        "Category" integer NOT NULL,
        "Code" character varying(40) NOT NULL,
        "Description" character varying(160) NOT NULL,
        "IsActive" boolean NOT NULL,
        "RequiresComment" boolean NOT NULL,
        "Version" bigint NOT NULL,
        CONSTRAINT "PK_production_reasons" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE TABLE production_rework_cases (
        "Id" uuid NOT NULL,
        "WorkOrderId" uuid NOT NULL,
        "BatchId" uuid NOT NULL,
        "WorkOrderStageId" uuid NOT NULL,
        "OriginResultId" uuid NOT NULL,
        "InitialQuantity" numeric(18,4) NOT NULL,
        "OriginAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_production_rework_cases" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_production_rework_cases_production_batch_results_OriginResu~" FOREIGN KEY ("OriginResultId") REFERENCES production_batch_results (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_production_rework_cases_production_batches_BatchId" FOREIGN KEY ("BatchId") REFERENCES production_batches (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_production_rework_cases_production_work_order_stages_WorkOr~" FOREIGN KEY ("WorkOrderStageId") REFERENCES production_work_order_stages (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_production_rework_cases_production_work_orders_WorkOrderId" FOREIGN KEY ("WorkOrderId") REFERENCES production_work_orders (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE TABLE production_rework_attempts (
        "Id" uuid NOT NULL,
        "ReworkCaseId" uuid NOT NULL,
        "ResultId" uuid NOT NULL,
        CONSTRAINT "PK_production_rework_attempts" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_production_rework_attempts_production_batch_results_ResultId" FOREIGN KEY ("ResultId") REFERENCES production_batch_results (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_production_rework_attempts_production_rework_cases_ReworkCa~" FOREIGN KEY ("ReworkCaseId") REFERENCES production_rework_cases ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE TABLE production_rework_retentions (
        "Id" uuid NOT NULL,
        "ReworkCaseId" uuid NOT NULL,
        "IssueLinkId" uuid,
        "WarehouseReservationId" uuid,
        "Quantity" numeric(18,4) NOT NULL,
        CONSTRAINT "PK_production_rework_retentions" PRIMARY KEY ("Id"),
        CONSTRAINT ck_rework_retention_source CHECK (("IssueLinkId" IS NULL) <> ("WarehouseReservationId" IS NULL) AND "Quantity" > 0),
        CONSTRAINT "FK_production_rework_retentions_production_material_issue_link~" FOREIGN KEY ("IssueLinkId") REFERENCES production_material_issue_links (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_production_rework_retentions_production_rework_cases_Rework~" FOREIGN KEY ("ReworkCaseId") REFERENCES production_rework_cases ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_production_rework_retentions_production_warehouse_reservati~" FOREIGN KEY ("WarehouseReservationId") REFERENCES production_warehouse_reservations (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    INSERT INTO production_reasons ("Id", "Category", "Code", "Description", "IsActive", "RequiresComment", "Version")
    VALUES ('55555555-0000-0000-0000-000000000001', 0, 'OTRO', 'Otro', TRUE, TRUE, 0);
    INSERT INTO production_reasons ("Id", "Category", "Code", "Description", "IsActive", "RequiresComment", "Version")
    VALUES ('55555555-0000-0000-0000-000000000002', 1, 'OTRO', 'Otro', TRUE, TRUE, 0);
    INSERT INTO production_reasons ("Id", "Category", "Code", "Description", "IsActive", "RequiresComment", "Version")
    VALUES ('55555555-0000-0000-0000-000000000003', 2, 'OTRO', 'Otro', TRUE, TRUE, 0);
    INSERT INTO production_reasons ("Id", "Category", "Code", "Description", "IsActive", "RequiresComment", "Version")
    VALUES ('55555555-0000-0000-0000-000000000004', 3, 'OTRO', 'Otro', TRUE, TRUE, 0);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE UNIQUE INDEX "IX_production_supply_request_lines_material_plan_id" ON production_supply_request_lines (material_plan_id) WHERE rework_case_id IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE INDEX "IX_production_supply_request_lines_rework_case_id" ON production_supply_request_lines (rework_case_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE INDEX "IX_production_execution_audits_AuthorizedByUserId" ON production_execution_audits ("AuthorizedByUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE UNIQUE INDEX "IX_production_execution_audits_OperationId" ON production_execution_audits ("OperationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE INDEX "IX_production_execution_audits_ResponsibleUserId" ON production_execution_audits ("ResponsibleUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE INDEX "IX_production_execution_audits_WorkOrderId_RecordedAt" ON production_execution_audits ("WorkOrderId", "RecordedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE UNIQUE INDEX "IX_production_reasons_Category_Code" ON production_reasons ("Category", "Code");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE UNIQUE INDEX "IX_production_rework_attempts_ResultId" ON production_rework_attempts ("ResultId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE INDEX "IX_production_rework_attempts_ReworkCaseId" ON production_rework_attempts ("ReworkCaseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE INDEX "IX_production_rework_cases_BatchId" ON production_rework_cases ("BatchId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE UNIQUE INDEX "IX_production_rework_cases_OriginResultId" ON production_rework_cases ("OriginResultId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE INDEX "IX_production_rework_cases_WorkOrderId" ON production_rework_cases ("WorkOrderId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE INDEX "IX_production_rework_cases_WorkOrderStageId" ON production_rework_cases ("WorkOrderStageId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE UNIQUE INDEX "IX_production_rework_retentions_IssueLinkId" ON production_rework_retentions ("IssueLinkId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE INDEX "IX_production_rework_retentions_ReworkCaseId" ON production_rework_retentions ("ReworkCaseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    CREATE UNIQUE INDEX "IX_production_rework_retentions_WarehouseReservationId" ON production_rework_retentions ("WarehouseReservationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    ALTER TABLE production_supply_request_lines ADD CONSTRAINT "FK_production_supply_request_lines_production_rework_cases_rew~" FOREIGN KEY (rework_case_id) REFERENCES production_rework_cases ("Id") ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    UPDATE production_work_orders SET original_target_quantity = target_quantity;
    DO $p5$
    DECLARE r record; candidates integer; chosen uuid; available numeric;
    BEGIN
      FOR r IN
        SELECT result.*, batch.work_order_id
        FROM production_batch_results result
        JOIN production_batches batch ON batch.id = result.batch_id
        JOIN production_work_orders orders ON orders.id = batch.work_order_id
        WHERE orders.status NOT IN ('Closed', 'Cancelled')
          AND NOT EXISTS (
            SELECT 1 FROM production_events reversal
            JOIN production_events original ON original.id = reversal.related_event_id
            WHERE reversal.type = 'ResultReversed' AND original.operation_id = result.operation_id)
        ORDER BY result.recorded_at, result.id
      LOOP
        IF NOT r.is_rework AND r.rework_quantity > 0 THEN
          INSERT INTO production_rework_cases
            ("Id", "WorkOrderId", "BatchId", "WorkOrderStageId", "OriginResultId", "InitialQuantity", "OriginAt")
          VALUES (r.id, r.work_order_id, r.batch_id, r.work_order_stage_id, r.id, r.rework_quantity, r.recorded_at);
        ELSIF r.is_rework THEN
          SELECT count(*) INTO candidates FROM production_rework_cases c
          WHERE c."BatchId" = r.batch_id AND c."WorkOrderStageId" = r.work_order_stage_id
            AND c."InitialQuantity" > COALESCE((SELECT sum(ar.good_quantity + ar.scrap_quantity)
              FROM production_rework_attempts a JOIN production_batch_results ar ON ar.id = a."ResultId"
              WHERE a."ReworkCaseId" = c."Id"), 0);
          IF candidates <> 1 THEN
            RAISE EXCEPTION 'P5: retrabajo histórico % tiene % orígenes posibles; concilie su procedencia antes de migrar', r.id, candidates;
          END IF;
          SELECT c."Id", c."InitialQuantity" - COALESCE((SELECT sum(ar.good_quantity + ar.scrap_quantity)
            FROM production_rework_attempts a JOIN production_batch_results ar ON ar.id = a."ResultId"
            WHERE a."ReworkCaseId" = c."Id"), 0) INTO chosen, available
          FROM production_rework_cases c
          WHERE c."BatchId" = r.batch_id AND c."WorkOrderStageId" = r.work_order_stage_id
            AND c."InitialQuantity" > COALESCE((SELECT sum(ar.good_quantity + ar.scrap_quantity)
              FROM production_rework_attempts a JOIN production_batch_results ar ON ar.id = a."ResultId"
              WHERE a."ReworkCaseId" = c."Id"), 0);
          IF r.input_quantity > available OR r.input_quantity <> r.good_quantity + r.rework_quantity + r.scrap_quantity THEN
            RAISE EXCEPTION 'P5: cantidades históricas inconciliables en resultado %', r.id;
          END IF;
          INSERT INTO production_rework_attempts ("Id", "ReworkCaseId", "ResultId") VALUES (r.id, chosen, r.id);
        END IF;
      END LOOP;
    END $p5$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915161501_Phase135ProductionExecution') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260915161501_Phase135ProductionExecution', '10.0.10');
    END IF;
END $EF$;
COMMIT;

