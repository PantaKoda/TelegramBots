# Repository Guidelines

## AGENTS.md Authority & Update Policy (Mandatory)

AGENTS.md is the **authoritative description of the system** for AI agents.

Any change that affects one or more of the following **MUST update AGENTS.md in the same commit or PR**:

- System architecture or data flow
- Introduction of new subsystems (e.g. PostgreSQL, OCR worker)
- Changes to the lifecycle of uploads or capture sessions
- New non-negotiable design constraints
- Changes to what is considered **current implementation** vs **planned / target architecture**

When implementing a feature:
- Update **Current Implementation Status** to reflect reality
- Update **Target Architecture & Intent** if goals or structure change
- Update **Non-Negotiable Design Rules** if new constraints are introduced

If a change does **not** require an AGENTS.md update, state this explicitly in the commit or PR description.

---

## Branching & Workflow Rules (Mandatory)

- The `main` branch must always remain deployable and reflect production.
- All new work must be done in **feature branches**.
- One feature branch = one conceptual change (e.g. sessions, PostgreSQL, OCR orchestration).
- AI agents must **NEVER** commit directly to `main`.
- Feature branches must not introduce partially implemented architecture.
- Merge to `main` only when the feature branch is complete and reviewed.

---

## Testing & Verification Rules (Mandatory)

### Baseline guarantees (must never break)
Do not merge to `main` unless all of the following still work end-to-end:

- Telegram webhook endpoint responds with `200 OK` for valid updates
- PNG document upload succeeds and results in an object in Cloudflare R2
- Invalid uploads are rejected (non-PNG, Telegram photo uploads)
- Optional webhook secret validation still works when enabled

### Test expectations
- If a change touches logic, add or extend automated tests where feasible:
  - Prefer **unit tests** for pure logic (validators, session state machine)
  - Prefer **integration tests** for persistence (PostgreSQL) and job transitions
  - Unit tests must not require real Telegram or real R2

- When automated tests do not yet exist:
  - Add a minimal test project **before** adding complex behavior (sessions, PostgreSQL, queueing)
  - At minimum, add smoke-level tests for:
    - session creation / close transitions
    - database repository read/write
    - idempotency (replaying the same Telegram update must not duplicate work)

- Every PR or commit must include a short **“How to test”** note:
  - commands run (e.g. `dotnet test`, `dotnet build`)
  - manual smoke steps if applicable

---

## Project Structure & Module Organization

- `TelegramBots.sln` — solution entry point
- `TelegramImageBot/` — webhook web app project
- `TelegramImageBot.Tests/` — integration / smoke tests for the webhook API
- `database/` — PostgreSQL schema bootstrap SQL files
- `TelegramImageBot/Program.cs` — minimal API, Telegram webhook handling, PNG validation, Cloudflare R2 upload
- `TelegramImageBot/appsettings.json` and `TelegramImageBot/appsettings.Development.json` — configuration defaults
- `TelegramImageBot/Properties/` — local launch settings
- `TelegramImageBot/bin/`, `TelegramImageBot/obj/` — build outputs (do not edit)

---

## Project Description

- The project extracts, stores, and tracks a user’s **daily work schedule** from a **mobile-only application**.
- There is **no web version and no public API**.
- The system is implemented as a **personal Telegram webhook bot**.
- Screenshots are sent via Telegram and stored in **Cloudflare R2** using the S3-compatible API.

Operational constraints:
- Uploads are restricted to **PNG files only**
- Telegram **photo uploads are rejected**; screenshots must be sent as **document files**
- Optional user restriction via `TELEGRAM_ALLOWED_USER_ID`
- Optional webhook validation via `TELEGRAM_WEBHOOK_SECRET`

---

## Build, Test, and Development Commands

- `dotnet restore TelegramImageBot/TelegramImageBot.csproj`
- `dotnet build TelegramImageBot/TelegramImageBot.csproj -c Release`
- `dotnet run --project TelegramImageBot/TelegramImageBot.csproj`
- `dotnet publish TelegramImageBot/TelegramImageBot.csproj -c Release -o out`
- `docker build -t telegram-image-bot -f TelegramImageBot/Dockerfile .`
- `dotnet test TelegramBots.sln -c Release`

---

## Coding Style & Naming Conventions

- Language: C# (use the language version configured in the project)
- Nullable reference types enabled and respected
- Indentation: 4 spaces, no tabs
- `PascalCase` for public types and members
- `camelCase` for locals and parameters
- Keep `Program.cs` minimal; extract helpers as the bot grows
- Prefer clarity and maintainability over cleverness

---

## Security & Configuration

- `TELEGRAM_BOT_TOKEN` must be set via environment variables or user secrets
- Optional: `TELEGRAM_WEBHOOK_SECRET`
- Optional: `TELEGRAM_ALLOWED_USER_ID`
- Required for R2:
  - `R2_ENDPOINT`
  - `R2_ACCESS_KEY_ID`
  - `R2_SECRET_ACCESS_KEY`
  - `R2_BUCKET_NAME`
- Optional: `R2_OBJECT_PREFIX` (default: `screenshots`)
- **Never commit secrets** to `appsettings*.json`

---

## Current Implementation Status (Authoritative)

As of now, the system implements **ONLY**:

- C# .NET Telegram webhook bot
- Accepts PNG screenshots sent as Telegram documents
- Validates PNG format
- Uploads images directly to Cloudflare R2
- PostgreSQL schema bootstrap SQL (`database/001_schedule_ingest_schema.sql`, `database/002_capture_session_single_open_per_user.sql`, `database/003_capture_image_require_open_session.sql`, `database/004_schedule_notification.sql`)
- PostgreSQL C# runtime foundation:
  - connection string wiring (`ConnectionStrings:Postgres` or `DATABASE_URL`)
  - repository layer for `capture_session`, `capture_image`, `day_schedule`, `schedule_version`
- Capture session lifecycle in webhook flow:
  - explicit multi-image mode:
    - `/start_session` opens (or reuses) a user session in `open` state
    - subsequent valid uploads are appended to the open session with deterministic `sequence`
    - `/close` or `/done` transitions the active session to `closed`
  - implicit single-image mode:
    - if no open session exists, a valid PNG upload creates a session, stores image with `sequence = 1`, and immediately closes that session
- DB-level grouping invariants:
  - at most one open session per user
  - images can be inserted only while their capture session state is `open`
- Explicit multi-image grouping by active open capture session
- Telegram command UX:
  - command menu is registered on startup (`/help`, `/start_session`, `/close`, `/done`)
  - `/help` returns an in-chat usage guide for single and multi-image flows
- OCR dispatch coordination foundation:
  - background dispatcher claims at most one eligible session at a time (`closed` + at least one image)
  - claim transition is atomic (`closed -> processing`) to prevent duplicate workers claiming the same session
  - dispatcher currently only claims/logs sessions and does not run OCR or mark `done`/`failed`
- Notification delivery dispatcher:
  - background worker polls `schedule_notification` for `pending` rows every few seconds
  - pending rows are claimed with `FOR UPDATE SKIP LOCKED`, sent through Telegram, then marked `sent` or `failed`
  - `message_text` is forwarded as-is (no formatting/parsing in C#), and delivered rows are never deleted

The following are **NOT implemented**:

- OCR
- Schedule parsing
- Versioning/update detection behavior in webhook flow

Everything below describes **target architecture** and is **not yet implemented**.

---

## Target Architecture & Intent

The project will evolve into a **schedule ingestion pipeline** with these properties:

- Screenshots are **transient inputs**, not business entities
- The core business entity is a **versioned daily schedule**
- Each calendar day may have multiple **immutable versions**
- Screenshots uploaded together are grouped into **explicit capture sessions**
- OCR runs **once per closed capture session**, never per image
- OCR is performed by a **separate Python service** using PaddleOCR
- Parsed schedules are immutable; updates create **new versions**
- PostgreSQL already acts as the source of truth for capture session state and will additionally evolve into:
  - the coordination / job queue mechanism for OCR
  - the storage for schedule versions
- Cloudflare R2 remains **blob storage only**

---

## Non-Negotiable Design Rules

The following constraints are intentional and must not be violated:

- Do **NOT** run OCR on each image upload
- Do **NOT** infer screenshot grouping heuristically
- Grouping must be **explicit** via capture sessions
- Do **NOT** stitch images together
- Do **NOT** overwrite previously parsed schedules
- Every upload attempt produces a **new immutable schedule version**
- Date identity must come from **OCR of UI text**, not filenames or timestamps
- PostgreSQL is the **state + coordination mechanism**
- Cloudflare R2 is **blob storage only**
- The Python OCR worker runs **out-of-process** (never embedded in ASP.NET)
