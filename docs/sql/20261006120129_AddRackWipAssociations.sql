START TRANSACTION;
CREATE TABLE location_rack_wip_associations (
    row_code character varying(1) NOT NULL,
    rack_number smallint NOT NULL,
    wip_area_id uuid NOT NULL,
    CONSTRAINT "PK_location_rack_wip_associations" PRIMARY KEY (row_code, rack_number),
    CONSTRAINT ck_location_rack_wip_identity CHECK (row_code ~ '^[A-Z]$' AND rack_number > 0),
    CONSTRAINT "FK_location_rack_wip_associations_locations_wip_area_id" FOREIGN KEY (wip_area_id) REFERENCES locations (id) ON DELETE RESTRICT
);

CREATE INDEX "IX_location_rack_wip_associations_wip_area_id" ON location_rack_wip_associations (wip_area_id);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261006120129_AddRackWipAssociations', '10.0.10');

COMMIT;
