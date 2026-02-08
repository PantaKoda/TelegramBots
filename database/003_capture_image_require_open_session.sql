BEGIN;

CREATE OR REPLACE FUNCTION schedule_ingest.capture_image_require_open_session()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    session_state schedule_ingest.capture_session_state;
BEGIN
    SELECT cs.state
    INTO session_state
    FROM schedule_ingest.capture_session cs
    WHERE cs.id = NEW.session_id
    FOR UPDATE;

    IF session_state IS NULL THEN
        RAISE EXCEPTION
            'Capture session % does not exist',
            NEW.session_id;
    END IF;

    IF session_state <> 'open' THEN
        RAISE EXCEPTION
            'Cannot add image to capture session % in state %',
            NEW.session_id,
            session_state;
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_capture_image_require_open_session
    ON schedule_ingest.capture_image;

CREATE TRIGGER trg_capture_image_require_open_session
    BEFORE INSERT ON schedule_ingest.capture_image
    FOR EACH ROW
    EXECUTE FUNCTION schedule_ingest.capture_image_require_open_session();

COMMIT;
