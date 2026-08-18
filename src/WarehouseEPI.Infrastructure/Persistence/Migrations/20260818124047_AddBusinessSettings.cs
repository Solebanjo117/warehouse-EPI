using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "business_settings",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    business_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    warehouse_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    warehouse_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    logo_file_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    logo_content_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    logo_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_settings", x => x.id);
                    table.CheckConstraint("ck_business_settings_singleton", "id = 1");
                    table.ForeignKey(
                        name: "FK_business_settings_users_updated_by_user_id",
                        column: x => x.updated_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_business_settings_updated_by_user_id",
                table: "business_settings",
                column: "updated_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "business_settings");
        }
    }
}
