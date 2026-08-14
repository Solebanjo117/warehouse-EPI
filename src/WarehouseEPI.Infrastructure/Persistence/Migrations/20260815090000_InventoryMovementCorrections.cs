using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WarehouseDbContext))]
[Migration("20260815090000_InventoryMovementCorrections")]
public partial class InventoryMovementCorrections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "inventory_movement_corrections",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                original_movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                reversal_movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                replacement_movement_id = table.Column<Guid>(type: "uuid", nullable: true),
                reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                authorized_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inventory_movement_corrections", x => x.id);
                table.CheckConstraint("ck_inventory_movement_corrections_type", "type IN ('REVERSAL', 'REPLACEMENT')");
                table.ForeignKey("FK_inventory_movement_corrections_inventory_movements_original_movement_id", x => x.original_movement_id, "inventory_movements", "id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_inventory_movement_corrections_inventory_movements_reversal_movement_id", x => x.reversal_movement_id, "inventory_movements", "id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_inventory_movement_corrections_inventory_movements_replacement_movement_id", x => x.replacement_movement_id, "inventory_movements", "id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_inventory_movement_corrections_users_requested_by_user_id", x => x.requested_by_user_id, "users", "id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_inventory_movement_corrections_users_authorized_by_user_id", x => x.authorized_by_user_id, "users", "id", onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex(name: "IX_inventory_movement_corrections_operation_id", table: "inventory_movement_corrections", column: "operation_id", unique: true);
        migrationBuilder.CreateIndex(name: "IX_inventory_movement_corrections_original_movement_id", table: "inventory_movement_corrections", column: "original_movement_id", unique: true);
        migrationBuilder.CreateIndex(name: "IX_inventory_movement_corrections_reversal_movement_id", table: "inventory_movement_corrections", column: "reversal_movement_id", unique: true);
        migrationBuilder.CreateIndex(name: "IX_inventory_movement_corrections_replacement_movement_id", table: "inventory_movement_corrections", column: "replacement_movement_id", unique: true, filter: "replacement_movement_id IS NOT NULL");
        migrationBuilder.CreateIndex(name: "IX_inventory_movement_corrections_requested_by_user_id", table: "inventory_movement_corrections", column: "requested_by_user_id");
        migrationBuilder.CreateIndex(name: "IX_inventory_movement_corrections_authorized_by_user_id", table: "inventory_movement_corrections", column: "authorized_by_user_id");
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "inventory_movement_corrections");
}
