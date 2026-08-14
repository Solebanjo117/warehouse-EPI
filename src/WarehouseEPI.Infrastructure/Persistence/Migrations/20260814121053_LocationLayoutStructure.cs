using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LocationLayoutStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM locations) THEN
                        RAISE EXCEPTION 'LocationLayoutStructure requiere locations vacía para evitar reinterpretar ubicaciones provisionales.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_locations_level",
                table: "locations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_locations_shelf",
                table: "locations");

            migrationBuilder.DropColumn(
                name: "aisle",
                table: "locations");

            migrationBuilder.DropColumn(
                name: "pallet_position",
                table: "locations");

            migrationBuilder.RenameColumn(
                name: "shelf",
                table: "locations",
                newName: "rack_number");

            migrationBuilder.RenameColumn(
                name: "level_number",
                table: "locations",
                newName: "pallet_number");

            migrationBuilder.AddColumn<string>(
                name: "block_reason",
                table: "locations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "locations",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "row_code",
                table: "locations",
                type: "character varying(1)",
                maxLength: 1,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_locations_row_code_rack_number_pallet_number",
                table: "locations",
                columns: new[] { "row_code", "rack_number", "pallet_number" },
                unique: true,
                filter: "kind = 'RACK'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_locations_block",
                table: "locations",
                sql: "(is_blocked = FALSE AND block_reason IS NULL) OR (is_active = TRUE AND is_blocked = TRUE AND block_reason IS NOT NULL AND btrim(block_reason) <> '')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_locations_code_normalized",
                table: "locations",
                sql: "code = upper(btrim(code)) AND code <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_locations_kind",
                table: "locations",
                sql: "kind IN ('RACK', 'AREA')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_locations_structure",
                table: "locations",
                sql: "(kind = 'RACK' AND row_code ~ '^[A-Z]$' AND rack_number > 0 AND pallet_number BETWEEN 1 AND 9 AND code = row_code || '-' || rack_number::text || '-' || pallet_number::text) OR (kind = 'AREA' AND row_code IS NULL AND rack_number IS NULL AND pallet_number IS NULL AND code ~ '^[A-Z0-9]([A-Z0-9-]*[A-Z0-9])?$')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_locations_row_code_rack_number_pallet_number",
                table: "locations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_locations_block",
                table: "locations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_locations_code_normalized",
                table: "locations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_locations_kind",
                table: "locations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_locations_structure",
                table: "locations");

            migrationBuilder.AddColumn<string>(
                name: "aisle",
                table: "locations",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pallet_position",
                table: "locations",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE locations SET aisle = row_code, pallet_position = pallet_number::text WHERE kind = 'RACK';");

            migrationBuilder.DropColumn(
                name: "block_reason",
                table: "locations");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "locations");

            migrationBuilder.DropColumn(
                name: "row_code",
                table: "locations");

            migrationBuilder.RenameColumn(
                name: "rack_number",
                table: "locations",
                newName: "shelf");

            migrationBuilder.RenameColumn(
                name: "pallet_number",
                table: "locations",
                newName: "level_number");

            migrationBuilder.Sql(
                "UPDATE locations SET level_number = ((level_number - 1) / 3) + 1 WHERE level_number IS NOT NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_locations_level",
                table: "locations",
                sql: "level_number IS NULL OR level_number > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_locations_shelf",
                table: "locations",
                sql: "shelf IS NULL OR shelf > 0");
        }
    }
}
