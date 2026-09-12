using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductionMaterialOrderLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "production_material_issue_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_order_stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_movement_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_material_issue_links", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_material_issue_links_inventory_movement_lines_in~",
                        column: x => x.inventory_movement_line_id,
                        principalTable: "inventory_movement_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_material_issue_links_production_work_order_stage~",
                        column: x => x.work_order_stage_id,
                        principalTable: "production_work_order_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_material_issue_links_production_work_orders_work~",
                        column: x => x.work_order_id,
                        principalTable: "production_work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_material_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_order_stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    responsible_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reverses_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_material_operations", x => x.id);
                    table.CheckConstraint("ck_production_material_operation_reversal", "(type = 'REVERSAL' AND reverses_operation_id IS NOT NULL) OR (type <> 'REVERSAL' AND reverses_operation_id IS NULL)");
                    table.ForeignKey(
                        name: "FK_production_material_operations_production_material_operatio~",
                        column: x => x.reverses_operation_id,
                        principalTable: "production_material_operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_material_operations_production_work_order_stages~",
                        column: x => x.work_order_stage_id,
                        principalTable: "production_work_order_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_material_operations_production_work_orders_work_~",
                        column: x => x.work_order_id,
                        principalTable: "production_work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_material_operations_users_responsible_user_id",
                        column: x => x.responsible_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_material_operation_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    production_material_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_movement_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_material_operation_lines", x => x.id);
                    table.CheckConstraint("ck_production_material_operation_line_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_production_material_operation_lines_inventory_movement_line~",
                        column: x => x.inventory_movement_line_id,
                        principalTable: "inventory_movement_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_material_operation_lines_production_material_iss~",
                        column: x => x.issue_link_id,
                        principalTable: "production_material_issue_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_material_operation_lines_production_material_ope~",
                        column: x => x.production_material_operation_id,
                        principalTable: "production_material_operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issue_links_inventory_movement_line_id",
                table: "production_material_issue_links",
                column: "inventory_movement_line_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issue_links_work_order_id_work_order_st~",
                table: "production_material_issue_links",
                columns: new[] { "work_order_id", "work_order_stage_id" });

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issue_links_work_order_stage_id",
                table: "production_material_issue_links",
                column: "work_order_stage_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_operation_lines_inventory_movement_line~",
                table: "production_material_operation_lines",
                column: "inventory_movement_line_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_material_operation_lines_issue_link_id",
                table: "production_material_operation_lines",
                column: "issue_link_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_operation_lines_production_material_ope~",
                table: "production_material_operation_lines",
                columns: new[] { "production_material_operation_id", "issue_link_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_material_operations_operation_id",
                table: "production_material_operations",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_material_operations_responsible_user_id",
                table: "production_material_operations",
                column: "responsible_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_operations_reverses_operation_id",
                table: "production_material_operations",
                column: "reverses_operation_id",
                unique: true,
                filter: "reverses_operation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_operations_work_order_id_work_order_sta~",
                table: "production_material_operations",
                columns: new[] { "work_order_id", "work_order_stage_id" });

            migrationBuilder.CreateIndex(
                name: "IX_production_material_operations_work_order_stage_id",
                table: "production_material_operations",
                column: "work_order_stage_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_material_operation_lines");

            migrationBuilder.DropTable(
                name: "production_material_issue_links");

            migrationBuilder.DropTable(
                name: "production_material_operations");
        }
    }
}
