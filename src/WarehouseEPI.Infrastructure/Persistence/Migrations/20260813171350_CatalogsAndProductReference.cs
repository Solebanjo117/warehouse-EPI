using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatalogsAndProductReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM products) THEN
                        RAISE EXCEPTION 'CatalogsAndProductReference requiere que products esté vacío.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "class_code",
                table: "products");

            migrationBuilder.DropColumn(
                name: "type_code",
                table: "products");

            migrationBuilder.AddColumn<string>(
                name: "external_reference",
                table: "products",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "product_class_id",
                table: "products",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "product_type_id",
                table: "products",
                type: "smallint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "product_classes",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_classes", x => x.id);
                    table.CheckConstraint("ck_product_classes_code_normalized", "code = upper(btrim(code)) AND code <> ''");
                });

            migrationBuilder.CreateTable(
                name: "product_types",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_types", x => x.id);
                    table.CheckConstraint("ck_product_types_code_normalized", "code = upper(btrim(code)) AND code <> ''");
                });

            migrationBuilder.InsertData(
                table: "product_classes",
                columns: new[] { "id", "code", "is_active", "name" },
                values: new object[,]
                {
                    { (short)1, "2-BBAGS", true, "2-BBAGS" },
                    { (short)2, "AQUA", true, "AQUA" },
                    { (short)3, "AQUATANK", true, "AQUATANK" },
                    { (short)4, "BAYRIG", true, "BAYRIG" },
                    { (short)5, "BCF", true, "BCF" },
                    { (short)6, "BERMS", true, "BERMS" },
                    { (short)7, "BIOSEAL", true, "BIOSEAL" },
                    { (short)8, "BIOSEAL-EPI", true, "BIOSEAL-EPI" },
                    { (short)9, "BODY BAGS", true, "BODY BAGS" },
                    { (short)10, "BODY BAGS-HARD BAGS", true, "BODY BAGS-HARD BAGS" },
                    { (short)11, "CBF", true, "CBF" },
                    { (short)12, "DUMPSTER LINER", true, "DUMPSTER LINER" },
                    { (short)13, "FC-HARDBAGS", true, "FC-HARDBAGS" },
                    { (short)14, "IWM", true, "IWM" },
                    { (short)15, "KPA", true, "KPA" },
                    { (short)16, "MISC", true, "MISC" },
                    { (short)17, "NUCLEAR BAGS", true, "NUCLEAR BAGS" },
                    { (short)18, "PACKAGING", true, "PACKAGING" },
                    { (short)19, "RAD BAGS", true, "RAD BAGS" },
                    { (short)20, "RM", true, "RM" },
                    { (short)21, "SPOUTED BAGS", true, "SPOUTED BAGS" },
                    { (short)22, "SUB", true, "SUB" },
                    { (short)23, "SUB-COMP", true, "SUB-COMP" },
                    { (short)24, "SUBASS", true, "SUBASS" },
                    { (short)25, "SUBASSEMBLY", true, "SUBASSEMBLY" },
                    { (short)26, "UNDERGARMENTS", true, "UNDERGARMENTS" }
                });

            migrationBuilder.InsertData(
                table: "product_types",
                columns: new[] { "id", "code", "is_active", "name" },
                values: new object[,]
                {
                    { (short)1, "FG", true, "Producto terminado" },
                    { (short)2, "RAW", true, "Materia prima" }
                });

            migrationBuilder.InsertData(
                table: "units",
                columns: new[] { "id", "allows_decimals", "code", "is_active", "name" },
                values: new object[,]
                {
                    { (short)2, true, "BX", true, "Caja" },
                    { (short)3, true, "BDL", true, "Bulto" },
                    { (short)4, true, "CTN", true, "Caja de embarque" },
                    { (short)5, true, "FT", true, "Pie" },
                    { (short)6, true, "GAL", true, "Galón" },
                    { (short)7, true, "5GAL", true, "Recipiente de 5 galones" },
                    { (short)8, true, "3GANG", true, "Grupo de 3" },
                    { (short)9, true, "KT", true, "Kit" },
                    { (short)10, true, "PR", true, "Par" },
                    { (short)11, true, "LB", true, "Libra" },
                    { (short)12, true, "RL", true, "Rollo" },
                    { (short)13, true, "SQFT", true, "Pie cuadrado" },
                    { (short)14, true, "MSI", true, "Mil pulgadas cuadradas" },
                    { (short)15, true, "YD", true, "Yarda" },
                    { (short)16, true, "IN", true, "Pulgada" },
                    { (short)17, true, "OZ", true, "Onza" }
                });

            migrationBuilder.UpdateData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)1,
                column: "allows_decimals",
                value: true);

            migrationBuilder.Sql(
                """
                SELECT setval(pg_get_serial_sequence('units', 'id'), (SELECT MAX(id) FROM units), true);
                SELECT setval(pg_get_serial_sequence('product_types', 'id'), (SELECT MAX(id) FROM product_types), true);
                SELECT setval(pg_get_serial_sequence('product_classes', 'id'), (SELECT MAX(id) FROM product_classes), true);
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_units_code_normalized",
                table: "units",
                sql: "code = upper(btrim(code)) AND code <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_products_product_class_id",
                table: "products",
                column: "product_class_id");

            migrationBuilder.CreateIndex(
                name: "IX_products_product_type_id",
                table: "products",
                column: "product_type_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_external_reference_trimmed",
                table: "products",
                sql: "external_reference IS NULL OR (external_reference = btrim(external_reference) AND external_reference <> '')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_sku_normalized",
                table: "products",
                sql: "sku = upper(btrim(sku)) AND sku <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_product_classes_code",
                table: "product_classes",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_types_code",
                table: "product_types",
                column: "code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_products_product_classes_product_class_id",
                table: "products",
                column: "product_class_id",
                principalTable: "product_classes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_products_product_types_product_type_id",
                table: "products",
                column: "product_type_id",
                principalTable: "product_types",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_products_product_classes_product_class_id",
                table: "products");

            migrationBuilder.DropForeignKey(
                name: "FK_products_product_types_product_type_id",
                table: "products");

            migrationBuilder.DropTable(
                name: "product_classes");

            migrationBuilder.DropTable(
                name: "product_types");

            migrationBuilder.DropCheckConstraint(
                name: "ck_units_code_normalized",
                table: "units");

            migrationBuilder.DropIndex(
                name: "IX_products_product_class_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_product_type_id",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_external_reference_trimmed",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_sku_normalized",
                table: "products");

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)2);

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)3);

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)4);

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)5);

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)6);

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)7);

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)8);

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)9);

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)10);

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)11);

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)12);

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)13);

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)14);

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)15);

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)16);

            migrationBuilder.DeleteData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)17);

            migrationBuilder.UpdateData(
                table: "units",
                keyColumn: "id",
                keyValue: (short)1,
                column: "allows_decimals",
                value: false);

            migrationBuilder.Sql(
                "SELECT setval(pg_get_serial_sequence('units', 'id'), (SELECT MAX(id) FROM units), true);");

            migrationBuilder.DropColumn(
                name: "external_reference",
                table: "products");

            migrationBuilder.DropColumn(
                name: "product_class_id",
                table: "products");

            migrationBuilder.DropColumn(
                name: "product_type_id",
                table: "products");

            migrationBuilder.AddColumn<string>(
                name: "class_code",
                table: "products",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "type_code",
                table: "products",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }
    }
}
