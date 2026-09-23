using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DailyProductionSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "production_daily_configuration",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    last_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    cutting_stage_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sewing_stage_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ready_to_pack_stage_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shift_1_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shift_2_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_daily_configuration", x => x.id);
                    table.CheckConstraint("ck_production_daily_configuration_singleton", "id = 1");
                    table.ForeignKey(
                        name: "FK_production_daily_configuration_production_shifts_shift_1_id",
                        column: x => x.shift_1_id,
                        principalTable: "production_shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_daily_configuration_production_shifts_shift_2_id",
                        column: x => x.shift_2_id,
                        principalTable: "production_shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_daily_configuration_production_stages_cutting_st~",
                        column: x => x.cutting_stage_id,
                        principalTable: "production_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_daily_configuration_production_stages_ready_to_p~",
                        column: x => x.ready_to_pack_stage_id,
                        principalTable: "production_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_daily_configuration_production_stages_sewing_sta~",
                        column: x => x.sewing_stage_id,
                        principalTable: "production_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_daily_configuration_users_updated_by_user_id",
                        column: x => x.updated_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_schedule_import_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    file_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    imported_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    week_count = table.Column<int>(type: "integer", nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    capture_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_schedule_import_batches", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_schedule_import_batches_users_imported_by_user_id",
                        column: x => x.imported_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_schedule_weeks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    week_start = table.Column<DateOnly>(type: "date", nullable: false),
                    week_end = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_schedule_weeks", x => x.id);
                    table.CheckConstraint("ck_production_schedule_week_dates", "week_end = week_start + 5");
                    table.ForeignKey(
                        name: "FK_production_schedule_weeks_users_closed_by_user_id",
                        column: x => x.closed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_schedule_weeks_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_schedule_weeks_users_published_by_user_id",
                        column: x => x.published_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_daily_captures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    week_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    area = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shift_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    imported_reporter = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    source_sheet = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    source_row = table.Column<int>(type: "integer", nullable: true),
                    responsible_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    reversed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reversed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reverse_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_daily_captures", x => x.id);
                    table.CheckConstraint("ck_production_daily_capture_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_production_daily_captures_production_schedule_weeks_week_id",
                        column: x => x.week_id,
                        principalTable: "production_schedule_weeks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_daily_captures_production_shifts_shift_id",
                        column: x => x.shift_id,
                        principalTable: "production_shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_daily_captures_production_stages_stage_id",
                        column: x => x.stage_id,
                        principalTable: "production_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_daily_captures_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_daily_captures_users_responsible_user_id",
                        column: x => x.responsible_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_daily_captures_users_reversed_by_user_id",
                        column: x => x.reversed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_schedule_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    week_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    planned_date = table.Column<DateOnly>(type: "date", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    order_reference_1 = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    order_reference_2 = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    order_reference_3 = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_carryover = table.Column<bool>(type: "boolean", nullable: false),
                    start_area = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_sheet = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    source_row = table.Column<int>(type: "integer", nullable: true),
                    is_cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_schedule_lines", x => x.id);
                    table.CheckConstraint("ck_production_schedule_line_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_production_schedule_lines_production_schedule_weeks_week_id",
                        column: x => x.week_id,
                        principalTable: "production_schedule_weeks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_schedule_lines_production_work_orders_work_order~",
                        column: x => x.work_order_id,
                        principalTable: "production_work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_schedule_lines_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_schedule_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    week_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: false),
                    after_json = table.Column<string>(type: "jsonb", nullable: false),
                    responsible_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_schedule_revisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_schedule_revisions_production_schedule_weeks_wee~",
                        column: x => x.week_id,
                        principalTable: "production_schedule_weeks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_schedule_revisions_users_responsible_user_id",
                        column: x => x.responsible_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_daily_capture_allocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    capture_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_order_stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    process_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    receive_operation_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_daily_capture_allocations", x => x.id);
                    table.CheckConstraint("ck_production_daily_capture_allocation_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_production_daily_capture_allocations_production_batch_resul~",
                        column: x => x.batch_result_id,
                        principalTable: "production_batch_results",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_daily_capture_allocations_production_daily_captu~",
                        column: x => x.capture_id,
                        principalTable: "production_daily_captures",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_production_daily_capture_allocations_production_schedule_li~",
                        column: x => x.schedule_line_id,
                        principalTable: "production_schedule_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_daily_capture_allocations_production_work_order_~",
                        column: x => x.work_order_stage_id,
                        principalTable: "production_work_order_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_daily_capture_allocations_production_work_orders~",
                        column: x => x.work_order_id,
                        principalTable: "production_work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "production_daily_configuration",
                columns: new[] { "id", "cutting_stage_id", "last_operation_id", "last_request_fingerprint", "ready_to_pack_stage_id", "sewing_stage_id", "shift_1_id", "shift_2_id", "updated_at", "updated_by_user_id", "version" },
                values: new object[] { (short)1, null, null, null, null, null, null, null, null, null, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_capture_allocations_batch_result_id",
                table: "production_daily_capture_allocations",
                column: "batch_result_id",
                unique: true,
                filter: "batch_result_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_capture_allocations_capture_id",
                table: "production_daily_capture_allocations",
                column: "capture_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_capture_allocations_process_operation_id",
                table: "production_daily_capture_allocations",
                column: "process_operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_capture_allocations_schedule_line_id",
                table: "production_daily_capture_allocations",
                column: "schedule_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_capture_allocations_work_order_id",
                table: "production_daily_capture_allocations",
                column: "work_order_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_capture_allocations_work_order_stage_id",
                table: "production_daily_capture_allocations",
                column: "work_order_stage_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_captures_operation_id",
                table: "production_daily_captures",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_captures_product_id",
                table: "production_daily_captures",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_captures_responsible_user_id",
                table: "production_daily_captures",
                column: "responsible_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_captures_reversed_by_user_id",
                table: "production_daily_captures",
                column: "reversed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_captures_shift_id",
                table: "production_daily_captures",
                column: "shift_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_captures_stage_id",
                table: "production_daily_captures",
                column: "stage_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_captures_week_id_effective_date_product_id",
                table: "production_daily_captures",
                columns: new[] { "week_id", "effective_date", "product_id" });

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_configuration_cutting_stage_id",
                table: "production_daily_configuration",
                column: "cutting_stage_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_configuration_ready_to_pack_stage_id",
                table: "production_daily_configuration",
                column: "ready_to_pack_stage_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_configuration_sewing_stage_id",
                table: "production_daily_configuration",
                column: "sewing_stage_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_configuration_shift_1_id",
                table: "production_daily_configuration",
                column: "shift_1_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_configuration_shift_2_id",
                table: "production_daily_configuration",
                column: "shift_2_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_configuration_updated_by_user_id",
                table: "production_daily_configuration",
                column: "updated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_schedule_import_batches_file_hash",
                table: "production_schedule_import_batches",
                column: "file_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_schedule_import_batches_imported_by_user_id",
                table: "production_schedule_import_batches",
                column: "imported_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_schedule_import_batches_operation_id",
                table: "production_schedule_import_batches",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_schedule_lines_product_id_planned_date",
                table: "production_schedule_lines",
                columns: new[] { "product_id", "planned_date" });

            migrationBuilder.CreateIndex(
                name: "IX_production_schedule_lines_week_id_sequence",
                table: "production_schedule_lines",
                columns: new[] { "week_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_schedule_lines_work_order_id",
                table: "production_schedule_lines",
                column: "work_order_id",
                unique: true,
                filter: "work_order_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_production_schedule_revisions_operation_id",
                table: "production_schedule_revisions",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_schedule_revisions_responsible_user_id",
                table: "production_schedule_revisions",
                column: "responsible_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_schedule_revisions_week_id_recorded_at",
                table: "production_schedule_revisions",
                columns: new[] { "week_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_production_schedule_weeks_closed_by_user_id",
                table: "production_schedule_weeks",
                column: "closed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_schedule_weeks_created_by_user_id",
                table: "production_schedule_weeks",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_schedule_weeks_operation_id",
                table: "production_schedule_weeks",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_schedule_weeks_published_by_user_id",
                table: "production_schedule_weeks",
                column: "published_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_schedule_weeks_week_start",
                table: "production_schedule_weeks",
                column: "week_start",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_daily_capture_allocations");

            migrationBuilder.DropTable(
                name: "production_daily_configuration");

            migrationBuilder.DropTable(
                name: "production_schedule_import_batches");

            migrationBuilder.DropTable(
                name: "production_schedule_revisions");

            migrationBuilder.DropTable(
                name: "production_daily_captures");

            migrationBuilder.DropTable(
                name: "production_schedule_lines");

            migrationBuilder.DropTable(
                name: "production_schedule_weeks");
        }
    }
}
