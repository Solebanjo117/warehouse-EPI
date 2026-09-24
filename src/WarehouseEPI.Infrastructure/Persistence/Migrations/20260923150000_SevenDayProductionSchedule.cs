using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WarehouseEPI.Infrastructure.Persistence;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WarehouseDbContext))]
[Migration("20260923150000_SevenDayProductionSchedule")]
public sealed class SevenDayProductionSchedule : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_production_schedule_week_dates", "production_schedule_weeks");
        migrationBuilder.Sql("UPDATE production_schedule_weeks SET week_end = week_start + 6");
        migrationBuilder.AddCheckConstraint("ck_production_schedule_week_dates", "production_schedule_weeks",
            "week_end = week_start + 6");
        migrationBuilder.AddColumn<string>("original_type", "production_schedule_lines", "character varying(120)", maxLength: 120, nullable: true);
        migrationBuilder.AddColumn<string>("original_annotation_1", "production_schedule_lines", "character varying(500)", maxLength: 500, nullable: true);
        migrationBuilder.AddColumn<string>("original_annotation_2", "production_schedule_lines", "character varying(500)", maxLength: 500, nullable: true);
        migrationBuilder.AddColumn<string>("original_annotation_1_kind", "production_schedule_lines", "character varying(12)", maxLength: 12, nullable: true);
        migrationBuilder.AddColumn<string>("original_annotation_2_kind", "production_schedule_lines", "character varying(12)", maxLength: 12, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
              IF EXISTS (SELECT 1 FROM production_schedule_lines WHERE EXTRACT(ISODOW FROM planned_date) = 7)
                 OR EXISTS (SELECT 1 FROM production_daily_captures WHERE EXTRACT(ISODOW FROM effective_date) = 7)
                 OR EXISTS (SELECT 1 FROM production_carryover_plans WHERE EXTRACT(ISODOW FROM planned_date) = 7)
              THEN RAISE EXCEPTION 'Hay datos de producción del domingo; revísalos antes de revertir el calendario.';
              END IF;
            END $$;
            """);
        migrationBuilder.DropColumn("original_type", "production_schedule_lines");
        migrationBuilder.DropColumn("original_annotation_1", "production_schedule_lines");
        migrationBuilder.DropColumn("original_annotation_2", "production_schedule_lines");
        migrationBuilder.DropColumn("original_annotation_1_kind", "production_schedule_lines");
        migrationBuilder.DropColumn("original_annotation_2_kind", "production_schedule_lines");
        migrationBuilder.DropCheckConstraint("ck_production_schedule_week_dates", "production_schedule_weeks");
        migrationBuilder.Sql("UPDATE production_schedule_weeks SET week_end = week_start + 5");
        migrationBuilder.AddCheckConstraint("ck_production_schedule_week_dates", "production_schedule_weeks",
            "week_end = week_start + 5");
    }
}
