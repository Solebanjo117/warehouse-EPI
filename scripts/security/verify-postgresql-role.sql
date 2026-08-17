\set ON_ERROR_STOP on

SELECT has_database_privilege(current_user, 'warehouseEPI', 'CONNECT') AS can_connect,
       has_schema_privilege(current_user, 'public', 'USAGE') AS can_use_schema,
       has_schema_privilege(current_user, 'public', 'CREATE') AS can_create_schema_objects;

SELECT rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls
FROM pg_roles
WHERE rolname = current_user;

BEGIN;
SELECT id FROM products LIMIT 1;
UPDATE inventory_balances SET updated_at = updated_at WHERE false;
ROLLBACK;
