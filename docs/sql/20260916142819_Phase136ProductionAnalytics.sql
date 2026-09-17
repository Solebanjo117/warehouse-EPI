START TRANSACTION;
ALTER TABLE production_stages ADD inactivity_alert_hours integer;

ALTER TABLE production_stages ADD rework_alert_hours integer;

ALTER TABLE production_stages ADD CONSTRAINT ck_production_stages_alert_hours CHECK ((inactivity_alert_hours IS NULL OR inactivity_alert_hours > 0) AND (rework_alert_hours IS NULL OR rework_alert_hours > 0));

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260916142819_Phase136ProductionAnalytics', '10.0.10');

COMMIT;

