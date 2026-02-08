BEGIN;

CREATE UNIQUE INDEX IF NOT EXISTS capture_session_single_open_per_user_uk
    ON schedule_ingest.capture_session (user_id)
    WHERE state = 'open'::schedule_ingest.capture_session_state;

COMMIT;
