using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationalExceptionCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "operational_exception_cases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    condition_key = table.Column<string>(type: "character varying(220)", maxLength: 220, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cycle_count_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    primary_text = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    secondary_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    target_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    assigned_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    first_detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operational_exception_cases", x => x.id);
                    table.CheckConstraint("ck_operational_exception_cases_category", "category IN ('NEGATIVE_INVENTORY','BELOW_MINIMUM','UNASSIGNED_BALANCE','RESTRICTED_INVENTORY','STAGNANT_INVENTORY','CYCLE_COUNT_STALE','CYCLE_COUNT_PENDING','AGED_WIP')");
                    table.CheckConstraint("ck_operational_exception_cases_resolution", "(status = 'RESOLVED' AND resolved_at IS NOT NULL) OR (status <> 'RESOLVED' AND resolved_at IS NULL)");
                    table.CheckConstraint("ck_operational_exception_cases_severity", "severity IN ('CRITICAL','WARNING','INFORMATION')");
                    table.CheckConstraint("ck_operational_exception_cases_status", "status IN ('NEW','IN_PROGRESS','WAITING','RESOLVED')");
                    table.ForeignKey(
                        name: "FK_operational_exception_cases_cycle_count_locations_cycle_cou~",
                        column: x => x.cycle_count_location_id,
                        principalTable: "cycle_count_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_operational_exception_cases_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_operational_exception_cases_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_operational_exception_cases_users_assigned_user_id",
                        column: x => x.assigned_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "operational_exception_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    operational_exception_case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    previous_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    current_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    previous_assigned_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_assigned_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operational_exception_events", x => x.id);
                    table.CheckConstraint("ck_operational_exception_events_type", "type IN ('DETECTED','TRIAGE_UPDATED','AUTO_RESOLVED')");
                    table.ForeignKey(
                        name: "FK_operational_exception_events_operational_exception_cases_op~",
                        column: x => x.operational_exception_case_id,
                        principalTable: "operational_exception_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_operational_exception_events_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_operational_exception_events_users_current_assigned_user_id",
                        column: x => x.current_assigned_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_operational_exception_events_users_previous_assigned_user_id",
                        column: x => x.previous_assigned_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_operational_exception_cases_assigned_user_id_status",
                table: "operational_exception_cases",
                columns: new[] { "assigned_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_operational_exception_cases_category_condition_key",
                table: "operational_exception_cases",
                columns: new[] { "category", "condition_key" },
                unique: true,
                filter: "resolved_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_operational_exception_cases_cycle_count_location_id",
                table: "operational_exception_cases",
                column: "cycle_count_location_id");

            migrationBuilder.CreateIndex(
                name: "IX_operational_exception_cases_location_id",
                table: "operational_exception_cases",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_operational_exception_cases_product_id",
                table: "operational_exception_cases",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_operational_exception_cases_status_severity_first_detected_~",
                table: "operational_exception_cases",
                columns: new[] { "status", "severity", "first_detected_at" });

            migrationBuilder.CreateIndex(
                name: "IX_operational_exception_events_actor_user_id",
                table: "operational_exception_events",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_operational_exception_events_current_assigned_user_id",
                table: "operational_exception_events",
                column: "current_assigned_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_operational_exception_events_operation_id",
                table: "operational_exception_events",
                column: "operation_id",
                unique: true,
                filter: "operation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_operational_exception_events_operational_exception_case_id_~",
                table: "operational_exception_events",
                columns: new[] { "operational_exception_case_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_operational_exception_events_previous_assigned_user_id",
                table: "operational_exception_events",
                column: "previous_assigned_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operational_exception_events");

            migrationBuilder.DropTable(
                name: "operational_exception_cases");
        }
    }
}
