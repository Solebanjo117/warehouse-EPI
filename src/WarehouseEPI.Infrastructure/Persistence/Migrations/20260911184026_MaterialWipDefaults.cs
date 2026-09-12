using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MaterialWipDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "default_wip_location_id",
                table: "production_stages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "default_wip_rack_number",
                table: "production_stages",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "default_wip_row_code",
                table: "production_stages",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "production_material_wip_defaults",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    production_stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    row_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    rack_number = table.Column<short>(type: "smallint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_material_wip_defaults", x => x.id);
                    table.CheckConstraint("ck_production_material_wip_defaults_shape", "(location_id IS NOT NULL AND row_code IS NULL AND rack_number IS NULL) OR (location_id IS NULL AND row_code IS NOT NULL AND rack_number IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_production_material_wip_defaults_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_material_wip_defaults_production_stages_producti~",
                        column: x => x.production_stage_id,
                        principalTable: "production_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_material_wip_defaults_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "production_material_wip_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authorized_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: false),
                    after_json = table.Column<string>(type: "jsonb", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_material_wip_revisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_material_wip_revisions_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_material_wip_revisions_users_authorized_by_user_~",
                        column: x => x.authorized_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_stages_default_wip_location_id",
                table: "production_stages",
                column: "default_wip_location_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_production_stages_default_wip_shape",
                table: "production_stages",
                sql: "(default_wip_location_id IS NULL AND default_wip_row_code IS NULL AND default_wip_rack_number IS NULL) OR (default_wip_location_id IS NOT NULL AND default_wip_row_code IS NULL AND default_wip_rack_number IS NULL) OR (default_wip_location_id IS NULL AND default_wip_row_code IS NOT NULL AND default_wip_rack_number IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_wip_defaults_location_id",
                table: "production_material_wip_defaults",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_wip_defaults_product_id_production_stag~",
                table: "production_material_wip_defaults",
                columns: new[] { "product_id", "production_stage_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_material_wip_defaults_production_stage_id",
                table: "production_material_wip_defaults",
                column: "production_stage_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_wip_revisions_authorized_by_user_id",
                table: "production_material_wip_revisions",
                column: "authorized_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_wip_revisions_operation_id",
                table: "production_material_wip_revisions",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_material_wip_revisions_product_id",
                table: "production_material_wip_revisions",
                column: "product_id");

            migrationBuilder.AddForeignKey(
                name: "FK_production_stages_locations_default_wip_location_id",
                table: "production_stages",
                column: "default_wip_location_id",
                principalTable: "locations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_production_stages_locations_default_wip_location_id",
                table: "production_stages");

            migrationBuilder.DropTable(
                name: "production_material_wip_defaults");

            migrationBuilder.DropTable(
                name: "production_material_wip_revisions");

            migrationBuilder.DropIndex(
                name: "IX_production_stages_default_wip_location_id",
                table: "production_stages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_production_stages_default_wip_shape",
                table: "production_stages");

            migrationBuilder.DropColumn(
                name: "default_wip_location_id",
                table: "production_stages");

            migrationBuilder.DropColumn(
                name: "default_wip_rack_number",
                table: "production_stages");

            migrationBuilder.DropColumn(
                name: "default_wip_row_code",
                table: "production_stages");
        }
    }
}
