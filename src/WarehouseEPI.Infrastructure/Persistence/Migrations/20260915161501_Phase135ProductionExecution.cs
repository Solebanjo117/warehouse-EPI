using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase135ProductionExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_production_supply_request_lines_material_plan_id",
                table: "production_supply_request_lines");

            migrationBuilder.AddColumn<decimal>(
                name: "original_target_quantity",
                table: "production_work_orders",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "principal_closed_at",
                table: "production_work_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "rework_case_id",
                table: "production_supply_request_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "production_execution_audits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ResponsibleUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorizedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BeforeJson = table.Column<string>(type: "jsonb", nullable: false),
                    AfterJson = table.Column<string>(type: "jsonb", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_execution_audits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_execution_audits_production_work_orders_WorkOrde~",
                        column: x => x.WorkOrderId,
                        principalTable: "production_work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_execution_audits_users_AuthorizedByUserId",
                        column: x => x.AuthorizedByUserId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_execution_audits_users_ResponsibleUserId",
                        column: x => x.ResponsibleUserId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_reasons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Description = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresComment = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_reasons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "production_rework_cases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderStageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginResultId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitialQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    OriginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_rework_cases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_rework_cases_production_batch_results_OriginResu~",
                        column: x => x.OriginResultId,
                        principalTable: "production_batch_results",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_rework_cases_production_batches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "production_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_rework_cases_production_work_order_stages_WorkOr~",
                        column: x => x.WorkOrderStageId,
                        principalTable: "production_work_order_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_rework_cases_production_work_orders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "production_work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_rework_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReworkCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_rework_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_rework_attempts_production_batch_results_ResultId",
                        column: x => x.ResultId,
                        principalTable: "production_batch_results",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_rework_attempts_production_rework_cases_ReworkCa~",
                        column: x => x.ReworkCaseId,
                        principalTable: "production_rework_cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_rework_retentions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReworkCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueLinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    WarehouseReservationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_rework_retentions", x => x.Id);
                    table.CheckConstraint("ck_rework_retention_source", "(\"IssueLinkId\" IS NULL) <> (\"WarehouseReservationId\" IS NULL) AND \"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_production_rework_retentions_production_material_issue_link~",
                        column: x => x.IssueLinkId,
                        principalTable: "production_material_issue_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_rework_retentions_production_rework_cases_Rework~",
                        column: x => x.ReworkCaseId,
                        principalTable: "production_rework_cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_rework_retentions_production_warehouse_reservati~",
                        column: x => x.WarehouseReservationId,
                        principalTable: "production_warehouse_reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "production_reasons",
                columns: new[] { "Id", "Category", "Code", "Description", "IsActive", "RequiresComment", "Version" },
                values: new object[,]
                {
                    { new Guid("55555555-0000-0000-0000-000000000001"), 0, "OTRO", "Otro", true, true, 0L },
                    { new Guid("55555555-0000-0000-0000-000000000002"), 1, "OTRO", "Otro", true, true, 0L },
                    { new Guid("55555555-0000-0000-0000-000000000003"), 2, "OTRO", "Otro", true, true, 0L },
                    { new Guid("55555555-0000-0000-0000-000000000004"), 3, "OTRO", "Otro", true, true, 0L }
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_request_lines_material_plan_id",
                table: "production_supply_request_lines",
                column: "material_plan_id",
                unique: true,
                filter: "rework_case_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_request_lines_rework_case_id",
                table: "production_supply_request_lines",
                column: "rework_case_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_execution_audits_AuthorizedByUserId",
                table: "production_execution_audits",
                column: "AuthorizedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_production_execution_audits_OperationId",
                table: "production_execution_audits",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_execution_audits_ResponsibleUserId",
                table: "production_execution_audits",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "IX_production_execution_audits_WorkOrderId_RecordedAt",
                table: "production_execution_audits",
                columns: new[] { "WorkOrderId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_production_reasons_Category_Code",
                table: "production_reasons",
                columns: new[] { "Category", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_rework_attempts_ResultId",
                table: "production_rework_attempts",
                column: "ResultId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_rework_attempts_ReworkCaseId",
                table: "production_rework_attempts",
                column: "ReworkCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_production_rework_cases_BatchId",
                table: "production_rework_cases",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_production_rework_cases_OriginResultId",
                table: "production_rework_cases",
                column: "OriginResultId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_rework_cases_WorkOrderId",
                table: "production_rework_cases",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_production_rework_cases_WorkOrderStageId",
                table: "production_rework_cases",
                column: "WorkOrderStageId");

            migrationBuilder.CreateIndex(
                name: "IX_production_rework_retentions_IssueLinkId",
                table: "production_rework_retentions",
                column: "IssueLinkId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_rework_retentions_ReworkCaseId",
                table: "production_rework_retentions",
                column: "ReworkCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_production_rework_retentions_WarehouseReservationId",
                table: "production_rework_retentions",
                column: "WarehouseReservationId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_production_supply_request_lines_production_rework_cases_rew~",
                table: "production_supply_request_lines",
                column: "rework_case_id",
                principalTable: "production_rework_cases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Reconstruct only from effective historical results. Ambiguous provenance must be
            // reconciled explicitly, never guessed from present inventory or arbitrary FIFO.
            migrationBuilder.Sql("""
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
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $p5$ BEGIN
                  IF EXISTS (SELECT 1 FROM production_execution_audits)
                     OR EXISTS (SELECT 1 FROM production_rework_cases)
                     OR EXISTS (SELECT 1 FROM production_supply_request_lines WHERE rework_case_id IS NOT NULL)
                     OR EXISTS (SELECT 1 FROM production_material_operations WHERE type = 'SCRAP') THEN
                    RAISE EXCEPTION 'P5 contiene historia operativa; el descenso requiere un procedimiento explícito sin pérdida';
                  END IF;
                END $p5$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_production_supply_request_lines_production_rework_cases_rew~",
                table: "production_supply_request_lines");

            migrationBuilder.DropTable(
                name: "production_execution_audits");

            migrationBuilder.DropTable(
                name: "production_reasons");

            migrationBuilder.DropTable(
                name: "production_rework_attempts");

            migrationBuilder.DropTable(
                name: "production_rework_retentions");

            migrationBuilder.DropTable(
                name: "production_rework_cases");

            migrationBuilder.DropIndex(
                name: "IX_production_supply_request_lines_material_plan_id",
                table: "production_supply_request_lines");

            migrationBuilder.DropIndex(
                name: "IX_production_supply_request_lines_rework_case_id",
                table: "production_supply_request_lines");

            migrationBuilder.DropColumn(
                name: "original_target_quantity",
                table: "production_work_orders");

            migrationBuilder.DropColumn(
                name: "principal_closed_at",
                table: "production_work_orders");

            migrationBuilder.DropColumn(
                name: "rework_case_id",
                table: "production_supply_request_lines");

            migrationBuilder.CreateIndex(
                name: "IX_production_supply_request_lines_material_plan_id",
                table: "production_supply_request_lines",
                column: "material_plan_id",
                unique: true);
        }
    }
}
