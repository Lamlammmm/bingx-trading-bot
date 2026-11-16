-- Additive schema for PostgreSQL/Supabase; run as the backend table owner.
-- Runtime creates the same tables automatically. Existing trading history is untouched.
BEGIN;
        CREATE TABLE IF NOT EXISTS trading_journal (
            trading_mode TEXT NOT NULL CHECK (trading_mode IN ('PAPER','LIVE')),
            event_id TEXT NOT NULL, trade_id TEXT NOT NULL, signal_id TEXT NULL,
            strategy_version TEXT NOT NULL, configuration_hash TEXT NOT NULL,
            symbol TEXT NOT NULL, kind TEXT NOT NULL, occurred_at TEXT NOT NULL, payload_json TEXT NOT NULL,
            PRIMARY KEY(trading_mode,event_id)
        );
        CREATE INDEX IF NOT EXISTS ix_trading_journal_mode_time ON trading_journal(trading_mode,occurred_at);
        CREATE INDEX IF NOT EXISTS ix_trading_journal_trade ON trading_journal(trading_mode,trade_id);
        CREATE TABLE IF NOT EXISTS strategy_versions (
            trading_mode TEXT NOT NULL CHECK (trading_mode IN ('PAPER','LIVE')), version TEXT NOT NULL,
            configuration_hash TEXT NOT NULL, registration_json TEXT NOT NULL, PRIMARY KEY(trading_mode,version)
        );
        CREATE TABLE IF NOT EXISTS strategy_active (
            trading_mode TEXT PRIMARY KEY CHECK (trading_mode IN ('PAPER','LIVE')), version TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS strategy_changes (
            change_id TEXT PRIMARY KEY, trading_mode TEXT NOT NULL, from_version TEXT NOT NULL,
            to_version TEXT NOT NULL, action TEXT NOT NULL, approved_by TEXT NOT NULL, occurred_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS trading_controls (
            trading_mode TEXT PRIMARY KEY CHECK (trading_mode IN ('PAPER','LIVE')),
            halted INTEGER NOT NULL, reason TEXT NOT NULL, updated_at TEXT NOT NULL
        );
                ALTER TABLE trading_journal ENABLE ROW LEVEL SECURITY;
                ALTER TABLE strategy_versions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE strategy_active ENABLE ROW LEVEL SECURITY;
                ALTER TABLE strategy_changes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE trading_controls ENABLE ROW LEVEL SECURITY;
                DO $$ DECLARE r text; t text; BEGIN
                  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname=r) THEN
                      FOREACH t IN ARRAY ARRAY['trading_journal','strategy_versions','strategy_active','strategy_changes','trading_controls'] LOOP
                        EXECUTE format('REVOKE ALL ON TABLE %I FROM %I',t,r);
                      END LOOP;
                    END IF;
                  END LOOP;
                END $$;
COMMIT;

