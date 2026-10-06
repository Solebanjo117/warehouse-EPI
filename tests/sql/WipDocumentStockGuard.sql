-- Execute only against a disposable restored copy. All probe writes roll back.
\set ON_ERROR_STOP on
BEGIN;
DO $$
DECLARE
    plate public.pallet_plates%ROWTYPE;
    balance public.inventory_balances%ROWTYPE;
    wip_id uuid;
    probe_id uuid := gen_random_uuid();
BEGIN
    IF current_database() NOT LIKE 'warehouse_epi_wip_guard_test_%' THEN
        RAISE EXCEPTION 'This regression requires a disposable test database';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public.wip_document_cutovers) THEN
        RAISE EXCEPTION 'The fixture must have documentary WIP enabled';
    END IF;
    SELECT id INTO STRICT wip_id FROM public.locations WHERE operational_role = 'WIP' LIMIT 1;
    SELECT p.* INTO STRICT plate FROM public.pallet_plates p
        JOIN public.locations l ON l.id = p."LocationId"
        WHERE l.operational_role IS DISTINCT FROM 'WIP' AND p."Quantity" > 0 LIMIT 1;
    SELECT b.* INTO STRICT balance FROM public.inventory_balances b
        JOIN public.locations l ON l.id = b.location_id
        WHERE l.operational_role IS DISTINCT FROM 'WIP' AND b.quantity > 0 LIMIT 1;

    -- The original function fails here with undefined_column on NEW.location_id.
    UPDATE public.pallet_plates SET "Quantity" = "Quantity" / 2 WHERE "Id" = plate."Id";
    UPDATE public.inventory_balances SET quantity = quantity / 2 WHERE id = balance.id;
    INSERT INTO public.pallet_plates
        ("Id", "ProductId", "LocationId", "OriginMovementId", "Quantity", "Version", "IsVoided", "CreatedAt")
        VALUES (probe_id, plate."ProductId", plate."LocationId", plate."OriginMovementId", 1, 1, false, now());
    UPDATE public.pallet_plates SET "Quantity" = 0, "LocationId" = wip_id WHERE "Id" = probe_id;
    BEGIN
        UPDATE public.pallet_plates SET "Quantity" = 1 WHERE "Id" = probe_id;
        RAISE EXCEPTION 'Positive WIP plate update was accepted';
    EXCEPTION WHEN check_violation THEN
        IF SQLERRM <> 'WIP is documentary and cannot hold inventory' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO public.pallet_plates
            ("Id", "ProductId", "LocationId", "OriginMovementId", "Quantity", "Version", "IsVoided", "CreatedAt")
            VALUES (gen_random_uuid(), plate."ProductId", wip_id, plate."OriginMovementId", 1, 1, false, now());
        RAISE EXCEPTION 'Positive WIP plate insert was accepted';
    EXCEPTION WHEN check_violation THEN
        IF SQLERRM <> 'WIP is documentary and cannot hold inventory' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE public.inventory_balances SET location_id = wip_id, quantity = 1 WHERE id = balance.id;
        RAISE EXCEPTION 'Positive WIP balance update was accepted';
    EXCEPTION WHEN check_violation THEN
        IF SQLERRM <> 'WIP is documentary and cannot hold inventory' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO public.inventory_balances (id, product_id, location_id, lot_id, quantity, updated_at)
            VALUES (gen_random_uuid(), balance.product_id, wip_id, balance.lot_id, 1, now());
        RAISE EXCEPTION 'Positive WIP balance insert was accepted';
    EXCEPTION WHEN check_violation THEN
        IF SQLERRM <> 'WIP is documentary and cannot hold inventory' THEN RAISE; END IF;
    END;
END $$;
ROLLBACK;
