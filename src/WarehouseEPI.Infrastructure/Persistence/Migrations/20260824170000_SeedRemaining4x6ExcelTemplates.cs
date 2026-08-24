using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Labels;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WarehouseDbContext))]
[Migration("20260824170000_SeedRemaining4x6ExcelTemplates")]
public sealed class SeedRemaining4x6ExcelTemplates : Migration
{
    private const string SeededAt = "2026-08-24T17:00:00Z";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var presets = LabelTemplatePresetCatalog.RemainingExcelTemplates;
        var codes = string.Join(", ", presets.Select(item => $"'{Sql(item.Code)}'"));

        migrationBuilder.Sql($$"""
            DO $migration$
            DECLARE duplicate_code text;
            BEGIN
                SELECT code INTO duplicate_code
                FROM label_templates
                WHERE code IN ({{codes}})
                ORDER BY code
                LIMIT 1;

                IF duplicate_code IS NOT NULL THEN
                    RAISE EXCEPTION 'No se pueden migrar los formatos Excel: el código % ya existe.', duplicate_code;
                END IF;
            END
            $migration$;
            """);

        foreach (var preset in presets)
        {
            var designJson = LabelDesignSerializer.Serialize(preset.Design);
            var reason = $"Plantilla inicial migrada desde 4X6 LABELS 2026.xlsx, hoja {preset.SourceSheet}.";
            migrationBuilder.Sql($$"""
                INSERT INTO label_templates
                    (id, code, current_published_version_id, created_at, updated_at)
                VALUES
                    ('{{preset.TemplateId}}', '{{Sql(preset.Code)}}', NULL, '{{SeededAt}}', '{{SeededAt}}');

                INSERT INTO label_template_versions
                    (id, template_id, version, name, size_preset, status, design_json,
                     created_by_user_id, published_by_user_id, retired_by_user_id,
                     created_at, updated_at, published_at, retired_at)
                VALUES
                    ('{{preset.VersionId}}', '{{preset.TemplateId}}', 1, '{{Sql(preset.Name)}}',
                     '{{SizeCode(preset.Size)}}', 'PUBLISHED', $label${{designJson}}$label$::jsonb,
                     NULL, NULL, NULL, '{{SeededAt}}', '{{SeededAt}}', '{{SeededAt}}', NULL);

                UPDATE label_templates
                SET current_published_version_id = '{{preset.VersionId}}'
                WHERE id = '{{preset.TemplateId}}';

                INSERT INTO label_template_events
                    (id, template_id, template_version_id, type, reason, recorded_at)
                VALUES
                    ('{{preset.EventId}}', '{{preset.TemplateId}}', '{{preset.VersionId}}',
                     'PUBLISHED', '{{Sql(reason)}}', '{{SeededAt}}');
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        var presets = LabelTemplatePresetCatalog.RemainingExcelTemplates;
        var templateIds = string.Join(", ", presets.Select(item => $"'{item.TemplateId}'"));
        var versionIds = string.Join(", ", presets.Select(item => $"'{item.VersionId}'"));
        var eventIds = string.Join(", ", presets.Select(item => $"'{item.EventId}'"));

        migrationBuilder.Sql($$"""
            DO $migration$
            DECLARE dependent_code text;
            BEGIN
                SELECT template.code INTO dependent_code
                FROM label_templates AS template
                WHERE template.id IN ({{templateIds}})
                  AND (
                    EXISTS (
                        SELECT 1 FROM label_template_versions AS version
                        WHERE version.template_id = template.id
                          AND version.id NOT IN ({{versionIds}})
                    )
                    OR EXISTS (
                        SELECT 1 FROM label_template_events AS event
                        WHERE event.template_id = template.id
                          AND event.id NOT IN ({{eventIds}})
                    )
                    OR EXISTS (
                        SELECT 1
                        FROM label_template_version_assets AS asset
                        INNER JOIN label_template_versions AS version
                            ON version.id = asset.template_version_id
                        WHERE version.template_id = template.id
                          AND asset.template_version_id IN ({{versionIds}})
                    )
                  )
                ORDER BY template.code
                LIMIT 1;

                IF dependent_code IS NOT NULL THEN
                    RAISE EXCEPTION 'No se puede revertir la migración de etiquetas: % tiene cambios administrativos posteriores.', dependent_code;
                END IF;
            END
            $migration$;

            DELETE FROM label_template_events
            WHERE id IN ({{eventIds}});

            UPDATE label_templates
            SET current_published_version_id = NULL
            WHERE id IN ({{templateIds}})
              AND current_published_version_id IN ({{versionIds}});

            DELETE FROM label_template_versions
            WHERE id IN ({{versionIds}});

            DELETE FROM label_templates
            WHERE id IN ({{templateIds}});
            """);
    }

    private static string Sql(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string SizeCode(LabelSizePreset size) => size switch
    {
        LabelSizePreset.SixByFourLandscape => "6X4_L",
        LabelSizePreset.FourBySixPortrait => "4X6_P",
        LabelSizePreset.ThreeByOneLandscape => "3X1_L",
        LabelSizePreset.FourByFourPointFivePortrait => "4X45_P",
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, null)
    };
}
