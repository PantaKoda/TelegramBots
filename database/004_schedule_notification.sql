BEGIN;

CREATE TABLE IF NOT EXISTS schedule_ingest.schedule_notification (
    id BIGSERIAL PRIMARY KEY,
    user_id BIGINT NOT NULL,
    message_text TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    status TEXT NOT NULL DEFAULT 'pending',
    sent_at TIMESTAMPTZ NULL,
    CONSTRAINT schedule_notification_status_chk
        CHECK (status IN ('pending', 'sent', 'failed'))
);

CREATE INDEX IF NOT EXISTS schedule_notification_pending_created_at_idx
    ON schedule_ingest.schedule_notification (created_at, id)
    WHERE status = 'pending';

CREATE INDEX IF NOT EXISTS schedule_notification_user_created_at_idx
    ON schedule_ingest.schedule_notification (user_id, created_at DESC);

COMMIT;
