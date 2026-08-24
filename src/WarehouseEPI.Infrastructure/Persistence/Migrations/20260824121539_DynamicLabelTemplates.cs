using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DynamicLabelTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "label_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    content_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_label_assets", x => x.id);
                    table.CheckConstraint("ck_label_assets_dimensions", "width BETWEEN 1 AND 4096 AND height BETWEEN 1 AND 4096");
                    table.CheckConstraint("ck_label_assets_size", "octet_length(content) BETWEEN 1 AND 1048576");
                    table.CheckConstraint("ck_label_assets_type", "content_type IN ('image/png','image/jpeg')");
                    table.ForeignKey(
                        name: "FK_label_assets_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "label_template_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    authorized_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_label_template_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_label_template_events_users_authorized_by_user_id",
                        column: x => x.authorized_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_label_template_events_users_requested_by_user_id",
                        column: x => x.requested_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "label_template_version_assets",
                columns: table => new
                {
                    template_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_label_template_version_assets", x => new { x.template_version_id, x.asset_id });
                    table.ForeignKey(
                        name: "FK_label_template_version_assets_label_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "label_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "label_template_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    size_preset = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    design_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    published_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retired_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_label_template_versions", x => x.id);
                    table.CheckConstraint("ck_label_template_versions_number", "version > 0");
                    table.CheckConstraint("ck_label_template_versions_size", "size_preset IN ('6X4_L','4X6_P','3X1_L','4X45_P')");
                    table.CheckConstraint("ck_label_template_versions_status", "status IN ('DRAFT','IN_VALIDATION','PUBLISHED','RETIRED')");
                    table.ForeignKey(
                        name: "FK_label_template_versions_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_label_template_versions_users_published_by_user_id",
                        column: x => x.published_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_label_template_versions_users_retired_by_user_id",
                        column: x => x.retired_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "label_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    current_published_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_label_templates", x => x.id);
                    table.CheckConstraint("ck_label_templates_code", "code = upper(btrim(code)) AND code ~ '^[A-Z0-9][A-Z0-9-]{2,59}$'");
                    table.ForeignKey(
                        name: "FK_label_templates_label_template_versions_current_published_v~",
                        column: x => x.current_published_version_id,
                        principalTable: "label_template_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_label_assets_created_by_user_id",
                table: "label_assets",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_label_assets_sha256",
                table: "label_assets",
                column: "sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_label_template_events_authorized_by_user_id",
                table: "label_template_events",
                column: "authorized_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_label_template_events_requested_by_user_id",
                table: "label_template_events",
                column: "requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_label_template_events_template_id_recorded_at",
                table: "label_template_events",
                columns: new[] { "template_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_label_template_events_template_version_id",
                table: "label_template_events",
                column: "template_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_label_template_version_assets_asset_id",
                table: "label_template_version_assets",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "IX_label_template_versions_created_by_user_id",
                table: "label_template_versions",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_label_template_versions_published_by_user_id",
                table: "label_template_versions",
                column: "published_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_label_template_versions_retired_by_user_id",
                table: "label_template_versions",
                column: "retired_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_label_template_versions_template_id",
                table: "label_template_versions",
                column: "template_id",
                unique: true,
                filter: "status IN ('DRAFT','IN_VALIDATION')");

            migrationBuilder.CreateIndex(
                name: "IX_label_template_versions_template_id_version",
                table: "label_template_versions",
                columns: new[] { "template_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_label_templates_code",
                table: "label_templates",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_label_templates_current_published_version_id",
                table: "label_templates",
                column: "current_published_version_id");

            migrationBuilder.AddForeignKey(
                name: "FK_label_template_events_label_template_versions_template_vers~",
                table: "label_template_events",
                column: "template_version_id",
                principalTable: "label_template_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_label_template_events_label_templates_template_id",
                table: "label_template_events",
                column: "template_id",
                principalTable: "label_templates",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_label_template_version_assets_label_template_versions_templ~",
                table: "label_template_version_assets",
                column: "template_version_id",
                principalTable: "label_template_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_label_template_versions_label_templates_template_id",
                table: "label_template_versions",
                column: "template_id",
                principalTable: "label_templates",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(
                """
                INSERT INTO label_templates (id, code, current_published_version_id, created_at, updated_at)
                VALUES ('60000000-0000-0000-0000-000000000001', 'LBL-6X4-ZEBRA', NULL, '2026-08-24T12:15:39Z', '2026-08-24T12:15:39Z');

                INSERT INTO label_template_versions
                    (id, template_id, version, name, size_preset, status, design_json, created_by_user_id, published_by_user_id, retired_by_user_id, created_at, updated_at, published_at, retired_at)
                VALUES
                    ('60000000-0000-0000-0000-000000000002', '60000000-0000-0000-0000-000000000001', 1, 'Caja 6×4', '6X4_L', 'PUBLISHED',
                    $label${"schemaVersion":1,"fields":[],"elements":[{"id":"10000000-0000-0000-0000-000000000001","type":"text","x":160,"y":150,"width":900,"height":220,"rotation":0,"zIndex":1,"text":"PART NO.","binding":null,"assetId":null,"fontFamily":"Arial","fontSize":12,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000002","type":"code128","x":1050,"y":150,"width":4750,"height":700,"rotation":0,"zIndex":1,"text":null,"binding":"product.sku","assetId":null,"fontFamily":"Arial","fontSize":18,"bold":false,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000003","type":"field","x":160,"y":830,"width":5640,"height":420,"rotation":0,"zIndex":1,"text":null,"binding":"product.sku","assetId":null,"fontFamily":"Arial","fontSize":24,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"center","blankLine":false},{"id":"10000000-0000-0000-0000-000000000004","type":"text","x":160,"y":1320,"width":1300,"height":220,"rotation":0,"zIndex":1,"text":"DESCRIPTION","binding":null,"assetId":null,"fontFamily":"Arial","fontSize":12,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000005","type":"field","x":160,"y":1540,"width":5640,"height":900,"rotation":0,"zIndex":1,"text":null,"binding":"product.description","assetId":null,"fontFamily":"Arial","fontSize":21,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"center","blankLine":false},{"id":"10000000-0000-0000-0000-000000000006","type":"text","x":160,"y":2550,"width":500,"height":200,"rotation":0,"zIndex":1,"text":"QTY","binding":null,"assetId":null,"fontFamily":"Arial","fontSize":11,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000007","type":"code128","x":160,"y":2750,"width":2050,"height":500,"rotation":0,"zIndex":1,"text":null,"binding":"input.quantity","assetId":null,"fontFamily":"Arial","fontSize":18,"bold":false,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000008","type":"field","x":2250,"y":2760,"width":850,"height":450,"rotation":0,"zIndex":1,"text":null,"binding":"input.quantity","assetId":null,"fontFamily":"Arial","fontSize":20,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"center","blankLine":false},{"id":"10000000-0000-0000-0000-000000000009","type":"text","x":3250,"y":2550,"width":1000,"height":200,"rotation":0,"zIndex":1,"text":"DATE MFG","binding":null,"assetId":null,"fontFamily":"Arial","fontSize":11,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000010","type":"field","x":3250,"y":2800,"width":1250,"height":400,"rotation":0,"zIndex":1,"text":null,"binding":"input.manufacturingDate","assetId":null,"fontFamily":"Arial","fontSize":14,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000011","type":"text","x":4700,"y":2550,"width":900,"height":200,"rotation":0,"zIndex":1,"text":"REPACK","binding":null,"assetId":null,"fontFamily":"Arial","fontSize":11,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000012","type":"field","x":4700,"y":2800,"width":900,"height":400,"rotation":0,"zIndex":1,"text":null,"binding":"input.isRepack","assetId":null,"fontFamily":"Arial","fontSize":18,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"center","blankLine":false},{"id":"10000000-0000-0000-0000-000000000013","type":"text","x":4700,"y":3650,"width":1100,"height":150,"rotation":0,"zIndex":1,"text":"LBL-6X4-ZEBRA \u00B7 v1","binding":null,"assetId":null,"fontFamily":"Arial","fontSize":7,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"right","blankLine":false}]}$label$::jsonb,
                    NULL, NULL, NULL, '2026-08-24T12:15:39Z', '2026-08-24T12:15:39Z', '2026-08-24T12:15:39Z', NULL);

                UPDATE label_templates
                SET current_published_version_id = '60000000-0000-0000-0000-000000000002'
                WHERE id = '60000000-0000-0000-0000-000000000001';

                INSERT INTO label_template_events (id, template_id, template_version_id, type, reason, recorded_at)
                VALUES ('60000000-0000-0000-0000-000000000003', '60000000-0000-0000-0000-000000000001', '60000000-0000-0000-0000-000000000002', 'PUBLISHED', 'Plantilla inicial migrada desde LBL-6X4-ZEBRA fija.', '2026-08-24T12:15:39Z');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_label_templates_label_template_versions_current_published_v~",
                table: "label_templates");

            migrationBuilder.DropTable(
                name: "label_template_events");

            migrationBuilder.DropTable(
                name: "label_template_version_assets");

            migrationBuilder.DropTable(
                name: "label_assets");

            migrationBuilder.DropTable(
                name: "label_template_versions");

            migrationBuilder.DropTable(
                name: "label_templates");
        }
    }
}
