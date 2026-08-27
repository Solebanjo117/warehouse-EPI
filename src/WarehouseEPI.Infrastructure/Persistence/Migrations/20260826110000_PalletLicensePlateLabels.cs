using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Labels;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WarehouseDbContext))]
[Migration("20260826110000_PalletLicensePlateLabels")]
public sealed class PalletLicensePlateLabels : Migration
{
    private const string SeededAt = "2026-08-26T11:00:00Z";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "kind", table: "label_templates", type: "character varying(30)", maxLength: 30, nullable: false, defaultValue: "PRODUCT_LABEL");
        migrationBuilder.AddCheckConstraint(name: "ck_label_templates_kind", table: "label_templates", sql: "kind IN ('PRODUCT_LABEL','PALLET_LICENSE_PLATE')");
        migrationBuilder.DropCheckConstraint(name: "ck_label_template_versions_size", table: "label_template_versions");
        migrationBuilder.AddCheckConstraint(name: "ck_label_template_versions_size", table: "label_template_versions", sql: "size_preset IN ('6X4_L','4X6_P','3X1_L','4X45_P','11X85_L')");

        var preset = PalletLicensePlatePresetCatalog.Initial;
        var design = LabelDesignSerializer.Serialize(preset.Design);
        migrationBuilder.Sql($$"""
            DO $migration$
            BEGIN
                IF EXISTS (SELECT 1 FROM label_templates WHERE code = '{{preset.Code}}') THEN
                    RAISE EXCEPTION 'No se puede migrar la placa de pallet: el código {{preset.Code}} ya existe.';
                END IF;
            END
            $migration$;

            INSERT INTO label_templates (id, code, kind, current_published_version_id, created_at, updated_at)
            VALUES ('{{preset.TemplateId}}', '{{preset.Code}}', 'PALLET_LICENSE_PLATE', NULL, '{{SeededAt}}', '{{SeededAt}}');

            INSERT INTO label_template_versions
                (id, template_id, version, name, size_preset, status, design_json, created_by_user_id, published_by_user_id, retired_by_user_id, created_at, updated_at, published_at, retired_at)
            VALUES ('{{preset.VersionId}}', '{{preset.TemplateId}}', 1, '{{preset.Name}}', '11X85_L', 'PUBLISHED', $label${{design}}$label$::jsonb, NULL, NULL, NULL, '{{SeededAt}}', '{{SeededAt}}', '{{SeededAt}}', NULL);

            UPDATE label_templates SET current_published_version_id = '{{preset.VersionId}}' WHERE id = '{{preset.TemplateId}}';

            INSERT INTO label_template_events (id, template_id, template_version_id, type, reason, recorded_at)
            VALUES ('{{preset.EventId}}', '{{preset.TemplateId}}', '{{preset.VersionId}}', 'PUBLISHED', 'Plantilla inicial migrada desde PALLET LICENSE PLATE.xlsx.', '{{SeededAt}}');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        var preset = PalletLicensePlatePresetCatalog.Initial;
        migrationBuilder.Sql($$"""
            DO $migration$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM label_template_versions WHERE template_id = '{{preset.TemplateId}}' AND id <> '{{preset.VersionId}}'
                    UNION ALL
                    SELECT 1 FROM label_template_events WHERE template_id = '{{preset.TemplateId}}' AND id <> '{{preset.EventId}}'
                ) THEN
                    RAISE EXCEPTION 'No se puede revertir la placa de pallet: existen cambios administrativos posteriores.';
                END IF;
            END
            $migration$;
            DELETE FROM label_template_events WHERE id = '{{preset.EventId}}';
            UPDATE label_templates SET current_published_version_id = NULL WHERE id = '{{preset.TemplateId}}' AND current_published_version_id = '{{preset.VersionId}}';
            DELETE FROM label_template_versions WHERE id = '{{preset.VersionId}}';
            DELETE FROM label_templates WHERE id = '{{preset.TemplateId}}';
            """);
        migrationBuilder.DropCheckConstraint(name: "ck_label_templates_kind", table: "label_templates");
        migrationBuilder.DropColumn(name: "kind", table: "label_templates");
        migrationBuilder.DropCheckConstraint(name: "ck_label_template_versions_size", table: "label_template_versions");
        migrationBuilder.AddCheckConstraint(name: "ck_label_template_versions_size", table: "label_template_versions", sql: "size_preset IN ('6X4_L','4X6_P','3X1_L','4X45_P')");
    }
}
