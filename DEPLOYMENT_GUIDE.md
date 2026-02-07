# Telegram Image Bot: Build, Deploy, and Operate Guide

This guide documents the exact process used to build, run, debug, and deploy this Telegram webhook bot to Coolify.

## 1. What this app does

- Receives Telegram updates through a webhook endpoint.
- Accepts only PNG files sent as Telegram documents.
- Validates PNG signature before upload.
- Uploads accepted files to Cloudflare R2 (S3-compatible API).
- Optional security controls:
  - `TELEGRAM_WEBHOOK_SECRET`
  - `TELEGRAM_ALLOWED_USER_ID`

## 2. Prerequisites

Create these before deployment:

1. A Telegram bot token from BotFather.
2. A Cloudflare R2 bucket.
3. R2 API credentials with write permission to that bucket.
4. A Coolify application pointing to this repository.
5. A public HTTPS domain for the app (for Telegram webhook delivery).

## 3. Repository and branch setup

This repository currently deploys from `master` (not `main`).

If Coolify is configured with `main`, deployment fails with:
- `fatal: Remote branch main not found in upstream origin`

Fix in Coolify:
- Branch: `master`

## 4. Coolify source and build settings

Use **Git-based source** in Coolify (public GitHub repository), and choose **Dockerfile build** for the application.

Recommended settings:

- Source method: Git repository
- Repository: `https://github.com/PantaKoda/TelegramBots.git`
- Branch: `master`
- Build pack/type: Dockerfile
- Dockerfile path: `TelegramImageBot/Dockerfile`
- Build context: repository root (`.`)

Why root context matters:
- The Dockerfile copies `TelegramImageBot/TelegramImageBot.csproj` from root paths.

## 5. Required runtime environment variables

Set these in Coolify runtime environment variables:

- `TELEGRAM_BOT_TOKEN`
- `R2_ENDPOINT`
- `R2_ACCESS_KEY_ID`
- `R2_SECRET_ACCESS_KEY`
- `R2_BUCKET_NAME`

Optional:

- `R2_OBJECT_PREFIX` (default `screenshots`)
- `TELEGRAM_WEBHOOK_SECRET`
- `TELEGRAM_ALLOWED_USER_ID`
- `ASPNETCORE_URLS=http://+:8080`

Important token formatting rules:

- No quotes around values.
- No trailing spaces/newlines.
- Use raw token format like `123456789:AA...`.

## 6. Health check settings in Coolify

Recommended:

- Port: `8080`
- Path: `/`
- Scheme: `http`

Expected result:
- `GET /` returns `200` with `"Telegram image bot is running."`

## 7. Register Telegram webhook

After deployment, register webhook URL once (or any time URL changes):

```powershell
curl.exe -X POST "https://api.telegram.org/bot<TELEGRAM_BOT_TOKEN>/setWebhook" -d "url=https://<your-domain>/telegram/hook" -d "secret_token=<TELEGRAM_WEBHOOK_SECRET>"
```

Verify:

```powershell
curl.exe "https://api.telegram.org/bot<TELEGRAM_BOT_TOKEN>/getWebhookInfo"
```

Expected:
- `url` is `https://<your-domain>/telegram/hook`

## 8. Runtime issue fixed during deployment

Observed error:

- `Amazon.S3.AmazonS3Exception: STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER not implemented`

Cause:
- Cloudflare R2 rejected AWS SDK streaming payload trailer behavior.

Fix applied in `TelegramImageBot/Program.cs` in `UploadToR2Async`:

```csharp
UseChunkEncoding = false,
DisablePayloadSigning = true
```

This change was committed and pushed to `master`.

## 9. End-to-end test flow

1. Open the bot chat in Telegram.
2. Send a PNG as a **Document/File** (not as Photo).
3. Expected bot behavior:
   - Valid PNG document: replies `Uploaded to R2: ...`
   - Photo message: asks for document upload
   - Non-PNG document: replies `Only PNG files are accepted.`
4. Confirm object appears in your R2 bucket.

## 10. Troubleshooting checklist

If deployment is degraded, check in this order:

1. Branch mismatch (`main` vs `master`).
2. App startup exceptions in runtime logs.
3. Invalid bot token (`Bot token invalid`).
4. Missing required env vars (`Missing required setting`).
5. HTTPS/TLS availability on public domain.
6. Webhook status via `getWebhookInfo`.
7. R2 upload compatibility flags in `PutObjectRequest`.

Notes:

- `No such container` during cleanup in Coolify logs is often non-fatal.
- Docker warning `--time deprecated` is non-fatal.

## 11. How to create a similar app from scratch

1. Create ASP.NET Core minimal API project.
2. Add Telegram.Bot + AWSSDK.S3 packages.
3. Implement endpoints:
   - `GET /` for health
   - `POST /telegram/hook` for updates
4. Validate webhook secret header (optional but recommended).
5. Restrict accepted uploads and validate file signature.
6. Upload to R2 with S3-compatible client and R2-compatible put settings.
7. Containerize with Dockerfile.
8. Deploy with Coolify using Git source + Dockerfile build, set runtime env vars, and expose HTTPS domain.
9. Register webhook with Telegram `setWebhook`.
10. Test with real Telegram document uploads.

## 12. Security practices

- Never commit real tokens or secrets.
- Store secrets only in runtime environment variables.
- Rotate bot token immediately if exposed.
- Use `TELEGRAM_WEBHOOK_SECRET` to protect webhook endpoint.
- Optionally restrict bot usage with `TELEGRAM_ALLOWED_USER_ID`.

