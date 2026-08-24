START TRANSACTION;
CREATE TABLE label_assets (
    id uuid NOT NULL,
    name character varying(120) NOT NULL,
    content_type character varying(20) NOT NULL,
    content bytea NOT NULL,
    sha256 character(64) NOT NULL,
    width integer NOT NULL,
    height integer NOT NULL,
    is_archived boolean NOT NULL,
    created_by_user_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT "PK_label_assets" PRIMARY KEY (id),
    CONSTRAINT ck_label_assets_dimensions CHECK (width BETWEEN 1 AND 4096 AND height BETWEEN 1 AND 4096),
    CONSTRAINT ck_label_assets_size CHECK (octet_length(content) BETWEEN 1 AND 1048576),
    CONSTRAINT ck_label_assets_type CHECK (content_type IN ('image/png','image/jpeg')),
    CONSTRAINT "FK_label_assets_users_created_by_user_id" FOREIGN KEY (created_by_user_id) REFERENCES users (id) ON DELETE RESTRICT
);

CREATE TABLE label_template_events (
    id uuid NOT NULL,
    template_id uuid NOT NULL,
    template_version_id uuid NOT NULL,
    type character varying(30) NOT NULL,
    requested_by_user_id uuid,
    authorized_by_user_id uuid,
    reason character varying(500),
    recorded_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT "PK_label_template_events" PRIMARY KEY (id),
    CONSTRAINT "FK_label_template_events_users_authorized_by_user_id" FOREIGN KEY (authorized_by_user_id) REFERENCES users (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_label_template_events_users_requested_by_user_id" FOREIGN KEY (requested_by_user_id) REFERENCES users (id) ON DELETE RESTRICT
);

CREATE TABLE label_template_version_assets (
    template_version_id uuid NOT NULL,
    asset_id uuid NOT NULL,
    CONSTRAINT "PK_label_template_version_assets" PRIMARY KEY (template_version_id, asset_id),
    CONSTRAINT "FK_label_template_version_assets_label_assets_asset_id" FOREIGN KEY (asset_id) REFERENCES label_assets (id) ON DELETE RESTRICT
);

CREATE TABLE label_template_versions (
    id uuid NOT NULL,
    template_id uuid NOT NULL,
    version integer NOT NULL,
    name character varying(120) NOT NULL,
    size_preset character varying(12) NOT NULL,
    status character varying(20) NOT NULL,
    design_json jsonb NOT NULL,
    created_by_user_id uuid,
    published_by_user_id uuid,
    retired_by_user_id uuid,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    published_at timestamp with time zone,
    retired_at timestamp with time zone,
    CONSTRAINT "PK_label_template_versions" PRIMARY KEY (id),
    CONSTRAINT ck_label_template_versions_number CHECK (version > 0),
    CONSTRAINT ck_label_template_versions_size CHECK (size_preset IN ('6X4_L','4X6_P','3X1_L','4X45_P')),
    CONSTRAINT ck_label_template_versions_status CHECK (status IN ('DRAFT','IN_VALIDATION','PUBLISHED','RETIRED')),
    CONSTRAINT "FK_label_template_versions_users_created_by_user_id" FOREIGN KEY (created_by_user_id) REFERENCES users (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_label_template_versions_users_published_by_user_id" FOREIGN KEY (published_by_user_id) REFERENCES users (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_label_template_versions_users_retired_by_user_id" FOREIGN KEY (retired_by_user_id) REFERENCES users (id) ON DELETE RESTRICT
);

CREATE TABLE label_templates (
    id uuid NOT NULL,
    code character varying(60) NOT NULL,
    current_published_version_id uuid,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT "PK_label_templates" PRIMARY KEY (id),
    CONSTRAINT ck_label_templates_code CHECK (code = upper(btrim(code)) AND code ~ '^[A-Z0-9][A-Z0-9-]{2,59}$'),
    CONSTRAINT "FK_label_templates_label_template_versions_current_published_v~" FOREIGN KEY (current_published_version_id) REFERENCES label_template_versions (id) ON DELETE RESTRICT
);

CREATE INDEX "IX_label_assets_created_by_user_id" ON label_assets (created_by_user_id);

CREATE UNIQUE INDEX "IX_label_assets_sha256" ON label_assets (sha256);

CREATE INDEX "IX_label_template_events_authorized_by_user_id" ON label_template_events (authorized_by_user_id);

CREATE INDEX "IX_label_template_events_requested_by_user_id" ON label_template_events (requested_by_user_id);

CREATE INDEX "IX_label_template_events_template_id_recorded_at" ON label_template_events (template_id, recorded_at);

CREATE INDEX "IX_label_template_events_template_version_id" ON label_template_events (template_version_id);

CREATE INDEX "IX_label_template_version_assets_asset_id" ON label_template_version_assets (asset_id);

CREATE INDEX "IX_label_template_versions_created_by_user_id" ON label_template_versions (created_by_user_id);

CREATE INDEX "IX_label_template_versions_published_by_user_id" ON label_template_versions (published_by_user_id);

CREATE INDEX "IX_label_template_versions_retired_by_user_id" ON label_template_versions (retired_by_user_id);

CREATE UNIQUE INDEX "IX_label_template_versions_template_id" ON label_template_versions (template_id) WHERE status IN ('DRAFT','IN_VALIDATION');

CREATE UNIQUE INDEX "IX_label_template_versions_template_id_version" ON label_template_versions (template_id, version);

CREATE UNIQUE INDEX "IX_label_templates_code" ON label_templates (code);

CREATE INDEX "IX_label_templates_current_published_version_id" ON label_templates (current_published_version_id);

ALTER TABLE label_template_events ADD CONSTRAINT "FK_label_template_events_label_template_versions_template_vers~" FOREIGN KEY (template_version_id) REFERENCES label_template_versions (id) ON DELETE RESTRICT;

ALTER TABLE label_template_events ADD CONSTRAINT "FK_label_template_events_label_templates_template_id" FOREIGN KEY (template_id) REFERENCES label_templates (id) ON DELETE RESTRICT;

ALTER TABLE label_template_version_assets ADD CONSTRAINT "FK_label_template_version_assets_label_template_versions_templ~" FOREIGN KEY (template_version_id) REFERENCES label_template_versions (id) ON DELETE CASCADE;

ALTER TABLE label_template_versions ADD CONSTRAINT "FK_label_template_versions_label_templates_template_id" FOREIGN KEY (template_id) REFERENCES label_templates (id) ON DELETE RESTRICT;

INSERT INTO label_templates (id, code, current_published_version_id, created_at, updated_at)
VALUES ('60000000-0000-0000-0000-000000000001', 'LBL-6X4-ZEBRA', NULL, '2026-08-24T12:15:39Z', '2026-08-24T12:15:39Z');

INSERT INTO label_template_versions
    (id, template_id, version, name, size_preset, status, design_json, created_by_user_id, published_by_user_id, retired_by_user_id, created_at, updated_at, published_at, retired_at)
VALUES
    ('60000000-0000-0000-0000-000000000002', '60000000-0000-0000-0000-000000000001', 1, 'Caja 6×4', '6X4_L', 'PUBLISHED',
    $label${"schemaVersion":1,"fields":[],"elements":[{"id":"10000000-0000-0000-0000-000000000001","type":"text","x":160,"y":150,"width":900,"height":220,"rotation":0,"zIndex":1,"text":"PART NO.","binding":null,"assetId":null,"fontFamily":"Arial","fontSize":12,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000002","type":"code128","x":1050,"y":150,"width":4750,"height":700,"rotation":0,"zIndex":1,"text":null,"binding":"product.sku","assetId":null,"fontFamily":"Arial","fontSize":18,"bold":false,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000003","type":"field","x":160,"y":830,"width":5640,"height":420,"rotation":0,"zIndex":1,"text":null,"binding":"product.sku","assetId":null,"fontFamily":"Arial","fontSize":24,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"center","blankLine":false},{"id":"10000000-0000-0000-0000-000000000004","type":"text","x":160,"y":1320,"width":1300,"height":220,"rotation":0,"zIndex":1,"text":"DESCRIPTION","binding":null,"assetId":null,"fontFamily":"Arial","fontSize":12,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000005","type":"field","x":160,"y":1540,"width":5640,"height":900,"rotation":0,"zIndex":1,"text":null,"binding":"product.description","assetId":null,"fontFamily":"Arial","fontSize":21,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"center","blankLine":false},{"id":"10000000-0000-0000-0000-000000000006","type":"text","x":160,"y":2550,"width":500,"height":200,"rotation":0,"zIndex":1,"text":"QTY","binding":null,"assetId":null,"fontFamily":"Arial","fontSize":11,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000007","type":"code128","x":160,"y":2750,"width":2050,"height":500,"rotation":0,"zIndex":1,"text":null,"binding":"input.quantity","assetId":null,"fontFamily":"Arial","fontSize":18,"bold":false,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000008","type":"field","x":2250,"y":2760,"width":850,"height":450,"rotation":0,"zIndex":1,"text":null,"binding":"input.quantity","assetId":null,"fontFamily":"Arial","fontSize":20,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"center","blankLine":false},{"id":"10000000-0000-0000-0000-000000000009","type":"text","x":3250,"y":2550,"width":1000,"height":200,"rotation":0,"zIndex":1,"text":"DATE MFG","binding":null,"assetId":null,"fontFamily":"Arial","fontSize":11,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000010","type":"field","x":3250,"y":2800,"width":1250,"height":400,"rotation":0,"zIndex":1,"text":null,"binding":"input.manufacturingDate","assetId":null,"fontFamily":"Arial","fontSize":14,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000011","type":"text","x":4700,"y":2550,"width":900,"height":200,"rotation":0,"zIndex":1,"text":"REPACK","binding":null,"assetId":null,"fontFamily":"Arial","fontSize":11,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"left","blankLine":false},{"id":"10000000-0000-0000-0000-000000000012","type":"field","x":4700,"y":2800,"width":900,"height":400,"rotation":0,"zIndex":1,"text":null,"binding":"input.isRepack","assetId":null,"fontFamily":"Arial","fontSize":18,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"center","blankLine":false},{"id":"10000000-0000-0000-0000-000000000013","type":"text","x":4700,"y":3650,"width":1100,"height":150,"rotation":0,"zIndex":1,"text":"LBL-6X4-ZEBRA \u00B7 v1","binding":null,"assetId":null,"fontFamily":"Arial","fontSize":7,"bold":true,"color":"#000000","backgroundColor":"#FFFFFF","borderWidth":1,"align":"right","blankLine":false}]}$label$::jsonb,
    NULL, NULL, NULL, '2026-08-24T12:15:39Z', '2026-08-24T12:15:39Z', '2026-08-24T12:15:39Z', NULL);

UPDATE label_templates
SET current_published_version_id = '60000000-0000-0000-0000-000000000002'
WHERE id = '60000000-0000-0000-0000-000000000001';

INSERT INTO label_template_events (id, template_id, template_version_id, type, reason, recorded_at)
VALUES ('60000000-0000-0000-0000-000000000003', '60000000-0000-0000-0000-000000000001', '60000000-0000-0000-0000-000000000002', 'PUBLISHED', 'Plantilla inicial migrada desde LBL-6X4-ZEBRA fija.', '2026-08-24T12:15:39Z');

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260824121539_DynamicLabelTemplates', '10.0.10');

COMMIT;

