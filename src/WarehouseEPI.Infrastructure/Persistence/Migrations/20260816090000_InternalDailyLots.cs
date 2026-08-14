using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WarehouseEPI.Infrastructure.Persistence.Migrations;

public partial class InternalDailyLots : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.DropCheckConstraint("ck_products_expiration_requires_lots", "products");
        m.DropColumn("tracks_expiration", "products");
        m.RenameColumn("expiration_date", "product_lots", "lot_date");
        m.AddColumn<string>("lot_allocation_mode", "inventory_movement_lines", "text", nullable: false, defaultValue: "NONE");
        m.AddColumn<DateOnly>("lot_date_snapshot", "inventory_balance_changes", "date", nullable: true);
        m.AddColumn<string>("lot_number_snapshot", "inventory_balance_changes", "character varying(100)", maxLength: 100, nullable: true);
        m.CreateTable("product_lot_date_changes", columns: t => new
        {
            id = t.Column<Guid>("uuid", nullable: false), operation_id = t.Column<Guid>("uuid", nullable: false),
            request_fingerprint = t.Column<string>("character(64)", fixedLength: true, maxLength: 64, nullable: false), product_lot_id = t.Column<Guid>("uuid", nullable: false),
            previous_lot_date = t.Column<DateOnly>("date", nullable: true), new_lot_date = t.Column<DateOnly>("date", nullable: true), reason = t.Column<string>("character varying(500)", maxLength: 500, nullable: false),
            requested_by_user_id = t.Column<Guid>("uuid", nullable: false), authorized_by_user_id = t.Column<Guid>("uuid", nullable: false), recorded_at = t.Column<DateTimeOffset>("timestamp with time zone", nullable: false, defaultValueSql: "now()")
        }, constraints: t => { t.PrimaryKey("PK_product_lot_date_changes", x => x.id); t.ForeignKey("FK_product_lot_date_changes_product_lots_product_lot_id", x => x.product_lot_id, "product_lots", "id", onDelete: ReferentialAction.Restrict); t.ForeignKey("FK_product_lot_date_changes_users_authorized_by_user_id", x => x.authorized_by_user_id, "users", "id", onDelete: ReferentialAction.Restrict); t.ForeignKey("FK_product_lot_date_changes_users_requested_by_user_id", x => x.requested_by_user_id, "users", "id", onDelete: ReferentialAction.Restrict); });
        m.CreateIndex("IX_product_lots_product_id_lot_date", "product_lots", new[] { "product_id", "lot_date" });
        m.CreateIndex("IX_product_lot_date_changes_operation_id", "product_lot_date_changes", "operation_id", unique: true);
        m.CreateIndex("IX_product_lot_date_changes_product_lot_id_recorded_at", "product_lot_date_changes", new[] { "product_lot_id", "recorded_at" });
        m.CreateIndex("IX_product_lot_date_changes_requested_by_user_id", "product_lot_date_changes", "requested_by_user_id");
        m.CreateIndex("IX_product_lot_date_changes_authorized_by_user_id", "product_lot_date_changes", "authorized_by_user_id");
    }
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("La migración de lotes internos no se revierte automáticamente.");
}
