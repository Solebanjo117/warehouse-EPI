using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProcessWipAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "production_process_configuration",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_process_configuration", x => x.id);
                    table.CheckConstraint("ck_production_process_configuration_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "production_process_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    production_stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authorized_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: false),
                    after_json = table.Column<string>(type: "jsonb", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_process_revisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_process_revisions_production_stages_production_s~",
                        column: x => x.production_stage_id,
                        principalTable: "production_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_process_revisions_users_authorized_by_user_id",
                        column: x => x.authorized_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_process_wip_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    production_stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    row_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    rack_number = table.Column<short>(type: "smallint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_process_wip_targets", x => x.id);
                    table.CheckConstraint("ck_production_process_wip_targets_shape", "(location_id IS NOT NULL AND row_code IS NULL AND rack_number IS NULL) OR (location_id IS NULL AND row_code IS NOT NULL AND rack_number IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_production_process_wip_targets_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_production_process_wip_targets_production_stages_production~",
                        column: x => x.production_stage_id,
                        principalTable: "production_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "production_process_configuration",
                columns: new[] { "id", "version" },
                values: new object[] { (short)1, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_production_process_revisions_authorized_by_user_id",
                table: "production_process_revisions",
                column: "authorized_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_process_revisions_operation_id",
                table: "production_process_revisions",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_process_revisions_production_stage_id",
                table: "production_process_revisions",
                column: "production_stage_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_process_wip_targets_location_id",
                table: "production_process_wip_targets",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_process_wip_targets_production_stage_id_location~",
                table: "production_process_wip_targets",
                columns: new[] { "production_stage_id", "location_id" },
                unique: true,
                filter: "location_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_production_process_wip_targets_production_stage_id_row_code~",
                table: "production_process_wip_targets",
                columns: new[] { "production_stage_id", "row_code", "rack_number" },
                unique: true,
                filter: "row_code IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_process_configuration");

            migrationBuilder.DropTable(
                name: "production_process_revisions");

            migrationBuilder.DropTable(
                name: "production_process_wip_targets");
        }
    }
}
