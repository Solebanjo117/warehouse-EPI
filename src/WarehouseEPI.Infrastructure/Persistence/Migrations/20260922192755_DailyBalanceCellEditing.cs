using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DailyBalanceCellEditing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "production_balance_edits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    WeekId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ResponsibleUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_balance_edits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_balance_edits_production_schedule_weeks_WeekId",
                        column: x => x.WeekId,
                        principalTable: "production_schedule_weeks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_balance_edits_users_ResponsibleUserId",
                        column: x => x.ResponsibleUserId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_balance_edit_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EditId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Area = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousTotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    RequestedTotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ReversedCaptureId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedCaptureId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_balance_edit_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_balance_edit_items_production_balance_edits_Edit~",
                        column: x => x.EditId,
                        principalTable: "production_balance_edits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_balance_edit_items_production_daily_captures_Cre~",
                        column: x => x.CreatedCaptureId,
                        principalTable: "production_daily_captures",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_balance_edit_items_production_daily_captures_Rev~",
                        column: x => x.ReversedCaptureId,
                        principalTable: "production_daily_captures",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_balance_edit_items_production_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "production_shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_balance_edit_items_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_balance_edit_items_CreatedCaptureId",
                table: "production_balance_edit_items",
                column: "CreatedCaptureId");

            migrationBuilder.CreateIndex(
                name: "IX_production_balance_edit_items_EditId",
                table: "production_balance_edit_items",
                column: "EditId");

            migrationBuilder.CreateIndex(
                name: "IX_production_balance_edit_items_ProductId",
                table: "production_balance_edit_items",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_production_balance_edit_items_ReversedCaptureId",
                table: "production_balance_edit_items",
                column: "ReversedCaptureId");

            migrationBuilder.CreateIndex(
                name: "IX_production_balance_edit_items_ShiftId",
                table: "production_balance_edit_items",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_production_balance_edits_OperationId",
                table: "production_balance_edits",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_balance_edits_ResponsibleUserId",
                table: "production_balance_edits",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "IX_production_balance_edits_WeekId",
                table: "production_balance_edits",
                column: "WeekId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_balance_edit_items");

            migrationBuilder.DropTable(
                name: "production_balance_edits");
        }
    }
}
