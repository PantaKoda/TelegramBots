BEGIN;

CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE SCHEMA IF NOT EXISTS schedule_ingest;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_type t
        JOIN pg_namespace n ON n.oid = t.typnamespace
        WHERE n.nspname = 'schedule_ingest'
          AND t.typname = 'capture_session_state'
    ) THEN
        CREATE TYPE schedule_ingest.capture_session_state AS ENUM (
            'open',
            'closed',
            'processing',
            'done',
            'failed'
        );
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS schedule_ingest.capture_session (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id BIGINT NOT NULL,
    state schedule_ingest.capture_session_state NOT NULL DEFAULT 'open',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    closed_at TIMESTAMPTZ NULL,
    error TEXT NULL,
    CONSTRAINT capture_session_closed_at_state_chk CHECK (
        (state = 'open' AND closed_at IS NULL)
        OR
        (state <> 'open' AND closed_at IS NOT NULL)
    ),
    CONSTRAINT capture_session_error_failed_chk CHECK (
        (state = 'failed' AND error IS NOT NULL)
        OR
        (state <> 'failed' AND error IS NULL)
    )
);

CREATE TABLE IF NOT EXISTS schedule_ingest.capture_image (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID NOT NULL,
    sequence INTEGER NOT NULL,
    r2_key TEXT NOT NULL,
    telegram_message_id BIGINT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT capture_image_session_fk
        FOREIGN KEY (session_id)
        REFERENCES schedule_ingest.capture_session (id)
        ON DELETE CASCADE,
    CONSTRAINT capture_image_sequence_positive_chk
        CHECK (sequence > 0),
    CONSTRAINT capture_image_session_sequence_uk
        UNIQUE (session_id, sequence)
);

CREATE TABLE IF NOT EXISTS schedule_ingest.day_schedule (
    user_id BIGINT NOT NULL,
    schedule_date DATE NOT NULL,
    current_version INTEGER NOT NULL,
    CONSTRAINT day_schedule_pk
        PRIMARY KEY (user_id, schedule_date),
    CONSTRAINT day_schedule_current_version_positive_chk
        CHECK (current_version > 0)
);

CREATE TABLE IF NOT EXISTS schedule_ingest.schedule_version (
    user_id BIGINT NOT NULL,
    schedule_date DATE NOT NULL,
    version INTEGER NOT NULL,
    session_id UUID NOT NULL,
    payload JSONB NOT NULL,
    payload_hash TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT schedule_version_pk
        PRIMARY KEY (user_id, schedule_date, version),
    CONSTRAINT schedule_version_session_uk
        UNIQUE (session_id),
    CONSTRAINT schedule_version_session_fk
        FOREIGN KEY (session_id)
        REFERENCES schedule_ingest.capture_session (id)
        ON DELETE RESTRICT,
    CONSTRAINT schedule_version_version_positive_chk
        CHECK (version > 0),
    CONSTRAINT schedule_version_payload_object_chk
        CHECK (jsonb_typeof(payload) = 'object'),
    CONSTRAINT schedule_version_payload_hash_not_blank_chk
        CHECK (length(btrim(payload_hash)) > 0)
);

CREATE INDEX IF NOT EXISTS capture_session_user_created_at_idx
    ON schedule_ingest.capture_session (user_id, created_at DESC);

CREATE INDEX IF NOT EXISTS capture_session_state_created_at_idx
    ON schedule_ingest.capture_session (state, created_at);

CREATE INDEX IF NOT EXISTS capture_session_ready_for_ocr_idx
    ON schedule_ingest.capture_session (created_at)
    WHERE state = 'closed';

CREATE INDEX IF NOT EXISTS capture_image_session_created_at_idx
    ON schedule_ingest.capture_image (session_id, created_at);

CREATE UNIQUE INDEX IF NOT EXISTS capture_image_r2_key_uk
    ON schedule_ingest.capture_image (r2_key);

CREATE UNIQUE INDEX IF NOT EXISTS capture_image_session_message_uk
    ON schedule_ingest.capture_image (session_id, telegram_message_id)
    WHERE telegram_message_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS schedule_version_user_date_created_at_idx
    ON schedule_ingest.schedule_version (user_id, schedule_date, created_at DESC);

CREATE INDEX IF NOT EXISTS schedule_version_payload_hash_idx
    ON schedule_ingest.schedule_version (user_id, schedule_date, payload_hash);

CREATE OR REPLACE FUNCTION schedule_ingest.capture_session_validate_transition()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.state = OLD.state THEN
        RETURN NEW;
    END IF;

    IF OLD.state = 'open' AND NEW.state IN ('closed', 'failed') THEN
        NULL;
    ELSIF OLD.state = 'closed' AND NEW.state IN ('processing', 'failed') THEN
        NULL;
    ELSIF OLD.state = 'processing' AND NEW.state IN ('done', 'failed') THEN
        NULL;
    ELSE
        RAISE EXCEPTION
            'Invalid capture_session transition from % to % for session %',
            OLD.state, NEW.state, OLD.id;
    END IF;

    IF NEW.state <> 'open' AND NEW.closed_at IS NULL THEN
        NEW.closed_at := now();
    END IF;

    IF NEW.state <> 'failed' THEN
        NEW.error := NULL;
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION schedule_ingest.schedule_version_validate_insert()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    existing_current_version INTEGER;
    session_state schedule_ingest.capture_session_state;
    session_user_id BIGINT;
BEGIN
    SELECT cs.state, cs.user_id
    INTO session_state, session_user_id
    FROM schedule_ingest.capture_session cs
    WHERE cs.id = NEW.session_id
    FOR UPDATE;

    IF session_state IS NULL THEN
        RAISE EXCEPTION
            'Session % does not exist',
            NEW.session_id;
    END IF;

    IF session_user_id <> NEW.user_id THEN
        RAISE EXCEPTION
            'Session % belongs to user %, but schedule version user is %',
            NEW.session_id,
            session_user_id,
            NEW.user_id;
    END IF;

    IF session_state NOT IN ('closed', 'processing', 'done') THEN
        RAISE EXCEPTION
            'Session % is in state %, expected closed/processing/done before writing schedule version',
            NEW.session_id,
            session_state;
    END IF;

    SELECT ds.current_version
    INTO existing_current_version
    FROM schedule_ingest.day_schedule ds
    WHERE ds.user_id = NEW.user_id
      AND ds.schedule_date = NEW.schedule_date
    FOR UPDATE;

    IF existing_current_version IS NULL THEN
        IF NEW.version <> 1 THEN
            RAISE EXCEPTION
                'First version for user % date % must be 1 (received %)',
                NEW.user_id,
                NEW.schedule_date,
                NEW.version;
        END IF;
    ELSIF NEW.version <> existing_current_version + 1 THEN
        RAISE EXCEPTION
            'Version for user % date % must be % (received %)',
            NEW.user_id,
            NEW.schedule_date,
            existing_current_version + 1,
            NEW.version;
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION schedule_ingest.schedule_version_sync_day_schedule()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO schedule_ingest.day_schedule (user_id, schedule_date, current_version)
    VALUES (NEW.user_id, NEW.schedule_date, NEW.version)
    ON CONFLICT (user_id, schedule_date)
    DO UPDATE
    SET current_version = GREATEST(
        schedule_ingest.day_schedule.current_version,
        EXCLUDED.current_version
    );

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION schedule_ingest.day_schedule_prevent_regression()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.current_version < OLD.current_version THEN
        RAISE EXCEPTION
            'day_schedule.current_version cannot decrease (old %, new %)',
            OLD.current_version,
            NEW.current_version;
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_capture_session_validate_transition
    ON schedule_ingest.capture_session;

CREATE TRIGGER trg_capture_session_validate_transition
    BEFORE UPDATE ON schedule_ingest.capture_session
    FOR EACH ROW
    EXECUTE FUNCTION schedule_ingest.capture_session_validate_transition();

DROP TRIGGER IF EXISTS trg_schedule_version_validate_insert
    ON schedule_ingest.schedule_version;

CREATE TRIGGER trg_schedule_version_validate_insert
    BEFORE INSERT ON schedule_ingest.schedule_version
    FOR EACH ROW
    EXECUTE FUNCTION schedule_ingest.schedule_version_validate_insert();

DROP TRIGGER IF EXISTS trg_schedule_version_sync_day_schedule
    ON schedule_ingest.schedule_version;

CREATE TRIGGER trg_schedule_version_sync_day_schedule
    AFTER INSERT ON schedule_ingest.schedule_version
    FOR EACH ROW
    EXECUTE FUNCTION schedule_ingest.schedule_version_sync_day_schedule();

DROP TRIGGER IF EXISTS trg_day_schedule_prevent_regression
    ON schedule_ingest.day_schedule;

CREATE TRIGGER trg_day_schedule_prevent_regression
    BEFORE UPDATE ON schedule_ingest.day_schedule
    FOR EACH ROW
    EXECUTE FUNCTION schedule_ingest.day_schedule_prevent_regression();

COMMIT;
