using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarehouseEPI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixWipDocumentStockGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.enforce_wip_document_stock() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    stock_location_id uuid;
                    stock_quantity numeric;
                BEGIN
                    IF EXISTS (SELECT 1 FROM public.wip_document_cutovers) THEN
                        IF TG_TABLE_NAME = 'pallet_plates' THEN
                            stock_location_id := NEW."LocationId";
                            stock_quantity := NEW."Quantity";
                        ELSIF TG_TABLE_NAME = 'inventory_balances' THEN
                            stock_location_id := NEW.location_id;
                            stock_quantity := NEW.quantity;
                        ELSE
                            RAISE EXCEPTION 'Unsupported table for documentary WIP stock guard';
                        END IF;
                        PERFORM id FROM public.locations WHERE id = stock_location_id FOR UPDATE;
                        IF stock_quantity <> 0 AND EXISTS (SELECT 1 FROM public.locations WHERE id = stock_location_id AND operational_role = 'WIP') THEN
                            RAISE EXCEPTION 'WIP is documentary and cannot hold inventory' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.enforce_wip_document_stock() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM wip_document_cutovers) THEN
                        PERFORM id FROM locations WHERE id = NEW.location_id FOR UPDATE;
                        IF NEW.quantity <> 0 AND EXISTS (SELECT 1 FROM locations WHERE id = NEW.location_id AND operational_role = 'WIP') THEN
                            RAISE EXCEPTION 'WIP is documentary and cannot hold inventory' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END $$;
                """);
        }
    }
}
