using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WarehouseDbContext))]
[Migration("20260825163000_AddLocationRackPhysicalPresence")]
public sealed class AddLocationRackPhysicalPresence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "is_physically_present",
            table: "locations",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.CreateTable(
            name: "location_rack_revisions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                row_code = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                rack_number = table.Column<short>(type: "smallint", nullable: false),
                reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                before_json = table.Column<string>(type: "jsonb", nullable: false),
                after_json = table.Column<string>(type: "jsonb", nullable: false),
                requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                authorized_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_location_rack_revisions", x => x.id);
                table.CheckConstraint("ck_location_rack_revisions_row", "row_code ~ '^[A-Z]$'");
                table.CheckConstraint("ck_location_rack_revisions_rack", "rack_number > 0");
                table.CheckConstraint("ck_location_rack_revisions_reason", "reason = btrim(reason) AND reason <> ''");
                table.ForeignKey("fk_location_rack_revisions_users_authorized_by_user_id", x => x.authorized_by_user_id, "users", "id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("fk_location_rack_revisions_users_requested_by_user_id", x => x.requested_by_user_id, "users", "id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("ix_location_rack_revisions_authorized_by_user_id", "location_rack_revisions", "authorized_by_user_id");
        migrationBuilder.CreateIndex("ix_location_rack_revisions_operation_id", "location_rack_revisions", "operation_id", unique: true);
        migrationBuilder.CreateIndex("ix_location_rack_revisions_requested_by_user_id", "location_rack_revisions", "requested_by_user_id");
        migrationBuilder.CreateIndex("ix_location_rack_revisions_row_code_rack_number_recorded_at", "location_rack_revisions", new[] { "row_code", "rack_number", "recorded_at" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "location_rack_revisions");
        migrationBuilder.DropColumn(name: "is_physically_present", table: "locations");
    }
}
