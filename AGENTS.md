# Repository Guidelines

## Project Structure & Module Organization
- `TelegramBots.sln` is the solution entry point.
- `TelegramImageBot/` contains the single web app project.
- `TelegramImageBot/Program.cs` hosts the minimal API, Telegram webhook handling, PNG validation, and Cloudflare R2 upload flow.
- `TelegramImageBot/appsettings.json` and `TelegramImageBot/appsettings.Development.json` hold configuration defaults.
- `TelegramImageBot/Properties/` contains local launch settings.
- `TelegramImageBot/bin/` and `TelegramImageBot/obj/` are build outputs and should not be edited.

## Project Description
- The app is a personal Telegram webhook bot that accepts image uploads and stores them in Cloudflare R2 through the S3-compatible API.
- Uploads are restricted to PNG files only.
- Photo uploads are rejected; screenshots must be sent as document files to preserve PNG quality.
- Optional user restriction is supported through `TELEGRAM_ALLOWED_USER_ID`.
- Optional webhook header validation is supported through `TELEGRAM_WEBHOOK_SECRET`.

## Build, Test, and Development Commands
- `dotnet restore TelegramImageBot/TelegramImageBot.csproj` restores NuGet packages.
- `dotnet build TelegramImageBot/TelegramImageBot.csproj -c Release` compiles the app.
- `dotnet run --project TelegramImageBot/TelegramImageBot.csproj` runs the webhook listener locally.
- `dotnet publish TelegramImageBot/TelegramImageBot.csproj -c Release -o out` produces a deployable build.
- `docker build -t telegram-image-bot -f TelegramImageBot/Dockerfile .` builds the container image.

## Coding Style & Naming Conventions
- Language: C# with nullable reference types enabled.
- Indentation: 4 spaces, no tabs.
- Use `PascalCase` for public types and methods, `camelCase` for locals and parameters.
- Keep Program.cs minimal; extract helpers into new files as the bot grows.

## Testing Guidelines
- No test project is present yet. If you add one, use `dotnet test` at the solution root.
- Name tests with clear intent, e.g., `Webhook_Rejects_InvalidSecret`.

## Commit & Pull Request Guidelines
- Git history is minimal and does not show a formal convention. Use short, imperative messages (e.g., "Add webhook validation").
- PRs should include: a concise summary, linked issue (if any), and notes on config changes or required env vars.

## Security & Configuration Tips
- Set `TELEGRAM_BOT_TOKEN` in environment variables or user secrets.
- Optional: set `TELEGRAM_WEBHOOK_SECRET` and ensure Telegram sends `X-Telegram-Bot-Api-Secret-Token`.
- Optional: set `TELEGRAM_ALLOWED_USER_ID` to process uploads only from your Telegram account.
- Required for R2 uploads: `R2_ENDPOINT`, `R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY`, `R2_BUCKET_NAME`.
- Optional for R2 key naming: `R2_OBJECT_PREFIX` (default: `screenshots`).
- Never commit real tokens or R2 keys in `appsettings*.json`.
