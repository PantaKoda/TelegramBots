# Change Tracker

This file tracks implementation progress and operational changes for this repository.

## 2026-02-07

### Deployment and Ops
- Diagnosed Coolify deployment failure caused by wrong branch configuration (`main` vs `master`).
- Verified Dockerfile-based build from Git source in Coolify.
- Confirmed runtime failures from invalid Telegram token format and fixed environment configuration.
- Registered webhook endpoint at `/telegram/hook` and validated webhook behavior.
- Diagnosed and fixed Cloudflare R2 upload compatibility issue:
  - Error: `STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER not implemented`
  - Fix in `TelegramImageBot/Program.cs`: `UseChunkEncoding = false`, `DisablePayloadSigning = true`.

### Documentation
- Added `DEPLOYMENT_GUIDE.md` with build, deploy, webhook, runtime checks, troubleshooting, and security guidance.
- Corrected guide to explicitly reflect Coolify Git-based source + Dockerfile build setup.

### Testing
- Added test project: `TelegramImageBot.Tests`.
- Added integration testing support with `Microsoft.AspNetCore.Mvc.Testing`.
- Added smoke tests:
  - `GET /` returns 200 and health message.
  - `POST /telegram/hook` without secret returns 401 when webhook secret is configured.
- Added `public partial class Program;` in `TelegramImageBot/Program.cs` for test host discovery.
- Verified test execution with `dotnet test TelegramBots.sln -c Release`.

## How to update this tracker
- Add a new date section per work session.
- Group updates under `Deployment and Ops`, `Documentation`, `Testing`, or other relevant headings.
- Keep entries short and action-oriented.
