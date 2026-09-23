using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWarehouseMapGeolocationCalibration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "warehouse_map_calibrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    layout_id = table.Column<short>(type: "smallint", nullable: false),
                    layout_version = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    origin_latitude = table.Column<double>(type: "double precision", nullable: false),
                    origin_longitude = table.Column<double>(type: "double precision", nullable: false),
                    a11 = table.Column<double>(type: "double precision", nullable: false),
                    a12 = table.Column<double>(type: "double precision", nullable: false),
                    a13 = table.Column<double>(type: "double precision", nullable: false),
                    a21 = table.Column<double>(type: "double precision", nullable: false),
                    a22 = table.Column<double>(type: "double precision", nullable: false),
                    a23 = table.Column<double>(type: "double precision", nullable: false),
                    fit_error_meters = table.Column<double>(type: "double precision", nullable: false),
                    check_error_meters = table.Column<double>(type: "double precision", nullable: false),
                    maximum_scale_svg_per_meter = table.Column<double>(type: "double precision", nullable: false),
                    algorithm_version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    published_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warehouse_map_calibrations", x => x.id);
                    table.CheckConstraint("ck_warehouse_map_calibration_algorithm", "algorithm_version = 'AFFINE_TANGENT_V1'");
                    table.CheckConstraint("ck_warehouse_map_calibration_errors", "fit_error_meters >= 0 AND check_error_meters >= 0 AND maximum_scale_svg_per_meter > 0");
                    table.CheckConstraint("ck_warehouse_map_calibration_status", "status IN ('ACTIVE', 'DISABLED')");
                    table.ForeignKey(
                        name: "FK_warehouse_map_calibrations_users_disabled_by_user_id",
                        column: x => x.disabled_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_warehouse_map_calibrations_users_published_by_user_id",
                        column: x => x.published_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_warehouse_map_calibrations_warehouse_map_layouts_layout_id",
                        column: x => x.layout_id,
                        principalTable: "warehouse_map_layouts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "warehouse_map_calibration_points",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    calibration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    map_x = table.Column<decimal>(type: "numeric(9,3)", precision: 9, scale: 3, nullable: false),
                    map_y = table.Column<decimal>(type: "numeric(9,3)", precision: 9, scale: 3, nullable: false),
                    latitude = table.Column<double>(type: "double precision", nullable: false),
                    longitude = table.Column<double>(type: "double precision", nullable: false),
                    accuracy_meters = table.Column<double>(type: "double precision", nullable: false),
                    dispersion_meters = table.Column<double>(type: "double precision", nullable: false),
                    sample_count = table.Column<int>(type: "integer", nullable: false),
                    samples_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warehouse_map_calibration_points", x => x.id);
                    table.CheckConstraint("ck_warehouse_map_calibration_point_kind", "kind IN ('REFERENCE', 'CHECK')");
                    table.CheckConstraint("ck_warehouse_map_calibration_point_samples", "sample_count > 0 AND accuracy_meters >= 0 AND dispersion_meters >= 0");
                    table.ForeignKey(
                        name: "FK_warehouse_map_calibration_points_warehouse_map_calibrations~",
                        column: x => x.calibration_id,
                        principalTable: "warehouse_map_calibrations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "warehouse_map_calibration_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    calibration_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    changes_json = table.Column<string>(type: "jsonb", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authorized_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warehouse_map_calibration_revisions", x => x.id);
                    table.CheckConstraint("ck_warehouse_map_calibration_revision_action", "action IN ('PUBLISH', 'DISABLE')");
                    table.ForeignKey(
                        name: "FK_warehouse_map_calibration_revisions_users_authorized_by_use~",
                        column: x => x.authorized_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_warehouse_map_calibration_revisions_users_requested_by_user~",
                        column: x => x.requested_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_warehouse_map_calibration_revisions_warehouse_map_calibrati~",
                        column: x => x.calibration_id,
                        principalTable: "warehouse_map_calibrations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_calibration_points_calibration_id_kind_name",
                table: "warehouse_map_calibration_points",
                columns: new[] { "calibration_id", "kind", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_calibration_revisions_authorized_by_user_id",
                table: "warehouse_map_calibration_revisions",
                column: "authorized_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_calibration_revisions_calibration_id",
                table: "warehouse_map_calibration_revisions",
                column: "calibration_id");

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_calibration_revisions_operation_id",
                table: "warehouse_map_calibration_revisions",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_calibration_revisions_recorded_at",
                table: "warehouse_map_calibration_revisions",
                column: "recorded_at");

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_calibration_revisions_requested_by_user_id",
                table: "warehouse_map_calibration_revisions",
                column: "requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_calibrations_disabled_by_user_id",
                table: "warehouse_map_calibrations",
                column: "disabled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_calibrations_layout_id_revision",
                table: "warehouse_map_calibrations",
                columns: new[] { "layout_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_calibrations_layout_id_status",
                table: "warehouse_map_calibrations",
                columns: new[] { "layout_id", "status" },
                unique: true,
                filter: "status = 'ACTIVE'");

            migrationBuilder.CreateIndex(
                name: "IX_warehouse_map_calibrations_published_by_user_id",
                table: "warehouse_map_calibrations",
                column: "published_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "warehouse_map_calibration_points");

            migrationBuilder.DropTable(
                name: "warehouse_map_calibration_revisions");

            migrationBuilder.DropTable(
                name: "warehouse_map_calibrations");
        }
    }
}
