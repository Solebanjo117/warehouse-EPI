using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FlexibleDailyProduction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_extra",
                table: "production_schedule_lines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_flexible",
                table: "production_daily_captures",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "reverse_fingerprint",
                table: "production_daily_captures",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reverse_operation_id",
                table: "production_daily_captures",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reconciled_at",
                table: "production_daily_capture_allocations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reconciled_by_user_id",
                table: "production_daily_capture_allocations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_captures_reverse_operation_id",
                table: "production_daily_captures",
                column: "reverse_operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_daily_capture_allocations_reconciled_by_user_id",
                table: "production_daily_capture_allocations",
                column: "reconciled_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_production_daily_capture_allocations_users_reconciled_by_us~",
                table: "production_daily_capture_allocations",
                column: "reconciled_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_production_daily_capture_allocations_users_reconciled_by_us~",
                table: "production_daily_capture_allocations");

            migrationBuilder.DropIndex(
                name: "IX_production_daily_captures_reverse_operation_id",
                table: "production_daily_captures");

            migrationBuilder.DropIndex(
                name: "IX_production_daily_capture_allocations_reconciled_by_user_id",
                table: "production_daily_capture_allocations");

            migrationBuilder.DropColumn(
                name: "is_extra",
                table: "production_schedule_lines");

            migrationBuilder.DropColumn(
                name: "is_flexible",
                table: "production_daily_captures");

            migrationBuilder.DropColumn(
                name: "reverse_fingerprint",
                table: "production_daily_captures");

            migrationBuilder.DropColumn(
                name: "reverse_operation_id",
                table: "production_daily_captures");

            migrationBuilder.DropColumn(
                name: "reconciled_at",
                table: "production_daily_capture_allocations");

            migrationBuilder.DropColumn(
                name: "reconciled_by_user_id",
                table: "production_daily_capture_allocations");
        }
    }
}
