START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mst.__ef_migrations_history WHERE "migration_id" = '20261026052832_Phase3NewsPeriodicScan') THEN
    ALTER TABLE trn.company_daily_news DROP CONSTRAINT ck_company_daily_news_scan_type;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mst.__ef_migrations_history WHERE "migration_id" = '20261026052832_Phase3NewsPeriodicScan') THEN
    ALTER TABLE trn.company_daily_news ADD CONSTRAINT ck_company_daily_news_scan_type CHECK (scan_type IN ('PREMARKET', 'INTRADAY_SPIKE', 'MANUAL', 'PERIODIC'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM mst.__ef_migrations_history WHERE "migration_id" = '20261026052832_Phase3NewsPeriodicScan') THEN
    INSERT INTO mst.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261026052832_Phase3NewsPeriodicScan', '10.0.14');
    END IF;
END $EF$;
COMMIT;

