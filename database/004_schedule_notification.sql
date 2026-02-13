BEGIN;

CREATE TABLE IF NOT EXISTS schedule_ingest.schedule_notification (
    notification_id TEXT PRIMARY KEY,
    user_id BIGINT NOT NULL,
    schedule_date DATE NOT NULL,
    source_session_id UUID NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'sent', 'failed')),
    notification_type TEXT NOT NULL CHECK (notification_type IN ('event', 'summary')),
    message TEXT NOT NULL,
    event_ids JSONB NOT NULL CHECK (jsonb_typeof(event_ids) = 'array'),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    sent_at TIMESTAMPTZ NULL
);

-- If a legacy shape exists (id/message_text), add compatible columns and backfill.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'schedule_ingest'
          AND table_name = 'schedule_notification'
          AND column_name = 'id'
    ) THEN
        IF NOT EXISTS (
            SELECT 1
            FROM information_schema.columns
            WHERE table_schema = 'schedule_ingest'
              AND table_name = 'schedule_notification'
              AND column_name = 'notification_id'
        ) THEN
            ALTER TABLE schedule_ingest.schedule_notification
                ADD COLUMN notification_id TEXT;
        END IF;

        EXECUTE '
            UPDATE schedule_ingest.schedule_notification
            SET notification_id = id::text
            WHERE notification_id IS NULL
        ';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'schedule_ingest'
          AND table_name = 'schedule_notification'
          AND column_name = 'message_text'
    ) THEN
        IF NOT EXISTS (
            SELECT 1
            FROM information_schema.columns
            WHERE table_schema = 'schedule_ingest'
              AND table_name = 'schedule_notification'
              AND column_name = 'message'
        ) THEN
            ALTER TABLE schedule_ingest.schedule_notification
                ADD COLUMN message TEXT;
        END IF;

        UPDATE schedule_ingest.schedule_notification
        SET message = message_text
        WHERE message IS NULL;
    END IF;
END $$;

ALTER TABLE schedule_ingest.schedule_notification
    ADD COLUMN IF NOT EXISTS status TEXT;

UPDATE schedule_ingest.schedule_notification
SET status = 'pending'
WHERE status IS NULL;

ALTER TABLE schedule_ingest.schedule_notification
    ALTER COLUMN status SET DEFAULT 'pending',
    ALTER COLUMN status SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'schedule_notification_status_check'
    ) THEN
        ALTER TABLE schedule_ingest.schedule_notification
            ADD CONSTRAINT schedule_notification_status_check
            CHECK (status IN ('pending', 'sent', 'failed'));
    END IF;
END $$;

ALTER TABLE schedule_ingest.schedule_notification
    ADD COLUMN IF NOT EXISTS schedule_date DATE;

UPDATE schedule_ingest.schedule_notification
SET schedule_date = CURRENT_DATE
WHERE schedule_date IS NULL;

ALTER TABLE schedule_ingest.schedule_notification
    ALTER COLUMN schedule_date SET NOT NULL;

ALTER TABLE schedule_ingest.schedule_notification
    ADD COLUMN IF NOT EXISTS source_session_id UUID;

UPDATE schedule_ingest.schedule_notification
SET source_session_id = gen_random_uuid()
WHERE source_session_id IS NULL;

ALTER TABLE schedule_ingest.schedule_notification
    ALTER COLUMN source_session_id SET NOT NULL;

ALTER TABLE schedule_ingest.schedule_notification
    ADD COLUMN IF NOT EXISTS notification_type TEXT;

UPDATE schedule_ingest.schedule_notification
SET notification_type = 'summary'
WHERE notification_type IS NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'schedule_notification_type_check'
    ) THEN
        ALTER TABLE schedule_ingest.schedule_notification
            ADD CONSTRAINT schedule_notification_type_check
            CHECK (notification_type IN ('event', 'summary'));
    END IF;
END $$;

ALTER TABLE schedule_ingest.schedule_notification
    ALTER COLUMN notification_type SET NOT NULL;

ALTER TABLE schedule_ingest.schedule_notification
    ADD COLUMN IF NOT EXISTS event_ids JSONB;

UPDATE schedule_ingest.schedule_notification
SET event_ids = '[]'::jsonb
WHERE event_ids IS NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'schedule_notification_event_ids_check'
    ) THEN
        ALTER TABLE schedule_ingest.schedule_notification
            ADD CONSTRAINT schedule_notification_event_ids_check
            CHECK (jsonb_typeof(event_ids) = 'array');
    END IF;
END $$;

ALTER TABLE schedule_ingest.schedule_notification
    ALTER COLUMN event_ids SET NOT NULL;

ALTER TABLE schedule_ingest.schedule_notification
    ALTER COLUMN notification_id SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'schedule_notification_pkey'
    ) THEN
        ALTER TABLE schedule_ingest.schedule_notification
            ADD CONSTRAINT schedule_notification_pkey PRIMARY KEY (notification_id);
    END IF;
END $$;

ALTER TABLE schedule_ingest.schedule_notification
    ALTER COLUMN message SET NOT NULL;

CREATE INDEX IF NOT EXISTS idx_schedule_notification_unsent_created
    ON schedule_ingest.schedule_notification (created_at ASC)
    WHERE status = 'pending';

CREATE INDEX IF NOT EXISTS idx_schedule_notification_user_date_created
    ON schedule_ingest.schedule_notification (user_id, schedule_date, created_at DESC);

COMMIT;
