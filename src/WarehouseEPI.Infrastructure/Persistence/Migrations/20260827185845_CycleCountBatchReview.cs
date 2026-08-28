using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CycleCountBatchReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "adjustment_reason",
                table: "cycle_count_locations",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "adjustment_reason_notes",
                table: "cycle_count_locations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "review_batch_id",
                table: "cycle_count_actions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cycle_count_review_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authorized_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authorized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cycle_count_review_batches", x => x.id);
                    table.ForeignKey(
                        name: "FK_cycle_count_review_batches_cycle_count_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "cycle_count_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cycle_count_review_batches_users_authorized_by_user_id",
                        column: x => x.authorized_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_actions_review_batch_id",
                table: "cycle_count_actions",
                column: "review_batch_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_review_batches_authorized_by_user_id",
                table: "cycle_count_review_batches",
                column: "authorized_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_review_batches_campaign_id_authorized_at",
                table: "cycle_count_review_batches",
                columns: new[] { "campaign_id", "authorized_at" });

            migrationBuilder.CreateIndex(
                name: "IX_cycle_count_review_batches_operation_id",
                table: "cycle_count_review_batches",
                column: "operation_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_cycle_count_actions_cycle_count_review_batches_review_batch_id",
                table: "cycle_count_actions",
                column: "review_batch_id",
                principalTable: "cycle_count_review_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_cycle_count_actions_cycle_count_review_batches_review_batch_id",
                table: "cycle_count_actions");

            migrationBuilder.DropTable(
                name: "cycle_count_review_batches");

            migrationBuilder.DropIndex(
                name: "IX_cycle_count_actions_review_batch_id",
                table: "cycle_count_actions");

            migrationBuilder.DropColumn(
                name: "adjustment_reason",
                table: "cycle_count_locations");

            migrationBuilder.DropColumn(
                name: "adjustment_reason_notes",
                table: "cycle_count_locations");

            migrationBuilder.DropColumn(
                name: "review_batch_id",
                table: "cycle_count_actions");
        }
    }
}
