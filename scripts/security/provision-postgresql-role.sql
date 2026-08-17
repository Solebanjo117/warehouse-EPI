\set ON_ERROR_STOP on

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'warehouse_epi_app') THEN
        CREATE ROLE warehouse_epi_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
    END IF;
END $$;

\password warehouse_epi_app

REVOKE ALL PRIVILEGES ON DATABASE "warehouseEPI" FROM warehouse_epi_app;
REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM warehouse_epi_app;
REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public FROM warehouse_epi_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

GRANT CONNECT ON DATABASE "warehouseEPI" TO warehouse_epi_app;
GRANT USAGE ON SCHEMA public TO warehouse_epi_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO warehouse_epi_app;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO warehouse_epi_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO warehouse_epi_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO warehouse_epi_app;

SELECT rolname, rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls
FROM pg_roles
WHERE rolname = 'warehouse_epi_app';
