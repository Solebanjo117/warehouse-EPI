using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistentProductionImportReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "production_import_drafts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    file_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_import_drafts", x => x.id);
                    table.CheckConstraint("ck_import_file_size", "octet_length(file_bytes) BETWEEN 1 AND 15728640");
                    table.ForeignKey(
                        name: "FK_production_import_drafts_production_schedule_import_batches~",
                        column: x => x.batch_id,
                        principalTable: "production_schedule_import_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_import_drafts_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_import_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    resolutions = table.Column<string>(type: "jsonb", nullable: false),
                    preview = table.Column<string>(type: "jsonb", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_import_revisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_production_import_revisions_production_import_drafts_draft_~",
                        column: x => x.draft_id,
                        principalTable: "production_import_drafts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_import_revisions_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_import_drafts_batch_id",
                table: "production_import_drafts",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_import_drafts_owner_id_updated_at",
                table: "production_import_drafts",
                columns: new[] { "owner_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_production_import_revisions_actor_id",
                table: "production_import_revisions",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_production_import_revisions_draft_id_number",
                table: "production_import_revisions",
                columns: new[] { "draft_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_import_revisions_operation_id",
                table: "production_import_revisions",
                column: "operation_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_import_revisions");

            migrationBuilder.DropTable(
                name: "production_import_drafts");
        }
    }
}
