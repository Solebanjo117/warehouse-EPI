using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase135CycleCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements");

            migrationBuilder.CreateTable(
                name: "cycle_count_campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_action_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cycle_count_campaigns", x => x.id);
                    table.ForeignKey(
                        name: "FK_cycle_count_campaigns_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_campaigns_users_last_action_by_user_id",
                        column: x => x.last_action_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cycle_count_locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    adjustment_movement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_action_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cycle_count_locations", x => x.id);
                    table.ForeignKey(
                        name: "FK_cycle_count_locations_cycle_count_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "cycle_count_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_locations_inventory_movements_adjustment_moveme~",
                        column: x => x.adjustment_movement_id,
                        principalTable: "inventory_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_locations_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_locations_users_last_action_by_user_id",
                        column: x => x.last_action_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cycle_count_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cycle_count_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    submitted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cycle_count_attempts", x => x.id);
                    table.ForeignKey(
                        name: "FK_cycle_count_attempts_cycle_count_locations_cycle_count_loca~",
                        column: x => x.cycle_count_location_id,
                        principalTable: "cycle_count_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_attempts_users_started_by_user_id",
                        column: x => x.started_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_attempts_users_submitted_by_user_id",
                        column: x => x.submitted_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cycle_count_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_count_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cycle_count_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    responsible_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cycle_count_actions", x => x.id);
                    table.ForeignKey(
                        name: "FK_cycle_count_actions_cycle_count_attempts_cycle_count_attemp~",
                        column: x => x.cycle_count_attempt_id,
                        principalTable: "cycle_count_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_actions_cycle_count_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "cycle_count_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_actions_cycle_count_locations_cycle_count_locat~",
                        column: x => x.cycle_count_location_id,
                        principalTable: "cycle_count_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_actions_users_responsible_user_id",
                        column: x => x.responsible_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cycle_count_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_count_attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<short>(type: "smallint", nullable: false),
                    expected_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    expected_balance_version = table.Column<long>(type: "bigint", nullable: false),
                    counted_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    is_unexpected_product = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cycle_count_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_cycle_count_entries_cycle_count_attempts_cycle_count_attemp~",
                        column: x => x.cycle_count_attempt_id,
                        principalTable: "cycle_count_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_entries_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_entries_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements",
                sql: "(purpose = 'PRODUCTION_ISSUE' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL) OR (purpose = 'GENERAL_EXIT' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (purpose = 'WIP_WAREHOUSE_RETURN' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (purpose = 'STANDARD' AND operational_area_id IS NULL) OR (purpose = 'CYCLE_COUNT_ADJUSTMENT' AND type = 'ADJUSTMENT' AND operational_area_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements",
                sql: "purpose IN ('STANDARD', 'GENERAL_EXIT', 'PRODUCTION_ISSUE', 'WIP_WAREHOUSE_RETURN', 'CYCLE_COUNT_ADJUSTMENT')");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_actions_campaign_id_recorded_at",
                table: "cycle_count_actions",
                columns: new[] { "campaign_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_actions_operation_id",
                table: "cycle_count_actions",
                column: "operation_id",
                unique: true,
                filter: "operation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_actions_cycle_count_attempt_id",
                table: "cycle_count_actions",
                column: "cycle_count_attempt_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_actions_cycle_count_location_id",
                table: "cycle_count_actions",
                column: "cycle_count_location_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_actions_responsible_user_id",
                table: "cycle_count_actions",
                column: "responsible_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_attempts_cycle_count_location_id_attempt_number",
                table: "cycle_count_attempts",
                columns: new[] { "cycle_count_location_id", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_attempts_operation_id",
                table: "cycle_count_attempts",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_attempts_submission_operation_id",
                table: "cycle_count_attempts",
                column: "submission_operation_id",
                unique: true,
                filter: "submission_operation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_attempts_started_by_user_id",
                table: "cycle_count_attempts",
                column: "started_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_attempts_submitted_by_user_id",
                table: "cycle_count_attempts",
                column: "submitted_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_campaigns_created_by_user_id",
                table: "cycle_count_campaigns",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_campaigns_last_action_by_user_id",
                table: "cycle_count_campaigns",
                column: "last_action_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_campaigns_number",
                table: "cycle_count_campaigns",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_campaigns_operation_id",
                table: "cycle_count_campaigns",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_campaigns_status_created_at",
                table: "cycle_count_campaigns",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_entries_cycle_count_attempt_id_product_id",
                table: "cycle_count_entries",
                columns: new[] { "cycle_count_attempt_id", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_entries_product_id",
                table: "cycle_count_entries",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_entries_unit_id",
                table: "cycle_count_entries",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_locations_adjustment_movement_id",
                table: "cycle_count_locations",
                column: "adjustment_movement_id",
                unique: true,
                filter: "adjustment_movement_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_locations_campaign_id_location_id",
                table: "cycle_count_locations",
                columns: new[] { "campaign_id", "location_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_locations_last_action_by_user_id",
                table: "cycle_count_locations",
                column: "last_action_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_locations_location_id_status",
                table: "cycle_count_locations",
                columns: new[] { "location_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cycle_count_actions");

            migrationBuilder.DropTable(
                name: "cycle_count_entries");

            migrationBuilder.DropTable(
                name: "cycle_count_attempts");

            migrationBuilder.DropTable(
                name: "cycle_count_locations");

            migrationBuilder.DropTable(
                name: "cycle_count_campaigns");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_operational_shape",
                table: "inventory_movements",
                sql: "(purpose = 'PRODUCTION_ISSUE' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL) OR (purpose = 'GENERAL_EXIT' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (purpose = 'WIP_WAREHOUSE_RETURN' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (purpose = 'STANDARD' AND operational_area_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_movements_purpose",
                table: "inventory_movements",
                sql: "purpose IN ('STANDARD', 'GENERAL_EXIT', 'PRODUCTION_ISSUE', 'WIP_WAREHOUSE_RETURN')");
        }
    }
}
