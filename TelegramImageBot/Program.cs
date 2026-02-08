using Amazon.S3;
using Amazon.S3.Model;
using TelegramImageBot.Data;
using Telegram.Bot;
using Telegram.Bot.Types;

var builder = WebApplication.CreateBuilder(args);

var settings = LoadSettings(builder.Configuration);
var postgresEnabled = builder.Services.AddSchedulePersistence(builder.Configuration);
var bot = new TelegramBotClient(settings.BotToken);
var s3 = CreateS3Client(settings.R2);

var app = builder.Build();
var logger = app.Logger;

app.MapGet("/", () => Results.Ok("Telegram image bot is running."));
app.MapGet("/health/db", async Task<IResult> (IServiceProvider services, CancellationToken cancellationToken) =>
{
    var dataSource = services.GetService<Npgsql.NpgsqlDataSource>();
    if (dataSource is null)
    {
        return Results.Ok(new
        {
            status = "disabled",
            reason = "DATABASE_URL or ConnectionStrings:Postgres is not configured."
        });
    }

    try
    {
        await using var command = dataSource.CreateCommand("SELECT 1");
        _ = await command.ExecuteScalarAsync(cancellationToken);
        return Results.Ok(new { status = "ok" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database health check failed.");
        return Results.Problem("Database connectivity check failed.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/telegram/hook", async Task<IResult> (
    HttpRequest req,
    Update update,
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    if (!IsSecretValid(req, settings.WebhookSecret))
        return Results.Unauthorized();

    var message = update.Message;
    var chatId = message?.Chat.Id;
    if (message is null || chatId is null || message.From is null)
        return Results.Ok();

    if (settings.AllowedUserId is not null && message.From.Id != settings.AllowedUserId.Value)
        return Results.Ok();

    var captureSessionRepository = services.GetService<ICaptureSessionRepository>();
    var captureImageRepository = services.GetService<ICaptureImageRepository>();
    var captureSessionsEnabled = CanUseCaptureSessions(captureSessionRepository, captureImageRepository);

    if (message.Document is null)
    {
        if (IsCloseSessionCommand(message.Text))
        {
            if (!captureSessionsEnabled)
            {
                await bot.SendMessage(
                    chatId.Value,
                    "Capture sessions are unavailable. Configure PostgreSQL and DATABASE_URL first.",
                    cancellationToken: cancellationToken
                );
                return Results.Ok();
            }

            var closedSession = await captureSessionRepository!.CloseOpenForUserAsync(message.From.Id, cancellationToken);
            if (closedSession is null)
            {
                await bot.SendMessage(
                    chatId.Value,
                    "No open capture session to close.",
                    cancellationToken: cancellationToken
                );
                return Results.Ok();
            }

            var imageCount = await captureImageRepository!.CountBySessionAsync(closedSession.Id, cancellationToken);
            await bot.SendMessage(
                chatId.Value,
                $"Closed session {closedSession.Id} with {imageCount} image(s).",
                cancellationToken: cancellationToken
            );
            return Results.Ok();
        }

        if (message.Photo?.Any() == true)
        {
            await bot.SendMessage(
                chatId.Value,
                "Please send screenshots as PNG files (document upload), not as photos.",
                cancellationToken: cancellationToken
            );
        }

        return Results.Ok();
    }

    if (!LooksLikePng(message.Document))
    {
        await bot.SendMessage(
            chatId.Value,
            "Only PNG files are accepted.",
            cancellationToken: cancellationToken
        );
        return Results.Ok();
    }

    try
    {
        await using var imageStream = await DownloadFileAsync(bot, message.Document.FileId, cancellationToken);

        if (!HasPngSignature(imageStream))
        {
            await bot.SendMessage(
                chatId.Value,
                "The uploaded file is not a valid PNG.",
                cancellationToken: cancellationToken
            );
            return Results.Ok();
        }

        var objectKey = BuildObjectKey(settings.R2.ObjectPrefix, message.From.Id, message.Document.FileName);
        await UploadToR2Async(s3, settings.R2.BucketName, objectKey, imageStream, cancellationToken);

        string sessionInfoSuffix = string.Empty;
        if (captureSessionsEnabled)
        {
            var captureSession = await captureSessionRepository!.GetOrCreateOpenForUserAsync(message.From.Id, cancellationToken);
            var captureImage = await captureImageRepository!.CreateNextAsync(
                captureSession.Id,
                objectKey,
                message.MessageId,
                cancellationToken
            );

            sessionInfoSuffix = $"\nSession: {captureSession.Id}\nSequence: {captureImage.Sequence}";
        }

        await bot.SendMessage(
            chatId.Value,
            $"Uploaded to R2: {objectKey}{sessionInfoSuffix}",
            cancellationToken: cancellationToken
        );
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to process Telegram file update.");
        await bot.SendMessage(
            chatId.Value,
            "Upload failed. Check server logs.",
            cancellationToken: cancellationToken
        );
    }

    return Results.Ok();
});

logger.LogInformation("PostgreSQL persistence {State}.", postgresEnabled ? "enabled" : "disabled");

app.Run();

static BotSettings LoadSettings(IConfiguration configuration)
{
    var botToken = RequireSetting(configuration, "TELEGRAM_BOT_TOKEN");
    var webhookSecret = configuration["TELEGRAM_WEBHOOK_SECRET"];
    var allowedUserIdRaw = configuration["TELEGRAM_ALLOWED_USER_ID"];
    long? allowedUserId = null;

    if (!string.IsNullOrWhiteSpace(allowedUserIdRaw))
    {
        if (!long.TryParse(allowedUserIdRaw, out var parsedUserId))
            throw new InvalidOperationException("TELEGRAM_ALLOWED_USER_ID must be a valid integer.");

        allowedUserId = parsedUserId;
    }

    var r2 = new R2Settings(
        Endpoint: RequireSetting(configuration, "R2_ENDPOINT"),
        AccessKeyId: RequireSetting(configuration, "R2_ACCESS_KEY_ID"),
        SecretAccessKey: RequireSetting(configuration, "R2_SECRET_ACCESS_KEY"),
        BucketName: RequireSetting(configuration, "R2_BUCKET_NAME"),
        ObjectPrefix: configuration["R2_OBJECT_PREFIX"] ?? "screenshots"
    );

    return new BotSettings(botToken, webhookSecret, allowedUserId, r2);
}

static string RequireSetting(IConfiguration configuration, string key)
{
    var value = configuration[key];
    if (string.IsNullOrWhiteSpace(value))
        throw new InvalidOperationException($"Missing required setting: {key}");

    return value;
}

static bool IsSecretValid(HttpRequest request, string? expectedSecret)
{
    if (string.IsNullOrWhiteSpace(expectedSecret))
        return true;

    return request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var receivedSecret)
        && string.Equals(receivedSecret, expectedSecret, StringComparison.Ordinal);
}

static bool CanUseCaptureSessions(ICaptureSessionRepository? sessionRepository, ICaptureImageRepository? imageRepository)
{
    return sessionRepository is not null && imageRepository is not null;
}

static bool IsCloseSessionCommand(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
        return false;

    var command = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    return string.Equals(command, "/close", StringComparison.OrdinalIgnoreCase)
        || command?.StartsWith("/close@", StringComparison.OrdinalIgnoreCase) == true;
}

static IAmazonS3 CreateS3Client(R2Settings settings)
{
    var config = new AmazonS3Config
    {
        ServiceURL = settings.Endpoint,
        AuthenticationRegion = "auto",
        ForcePathStyle = true
    };

    return new AmazonS3Client(settings.AccessKeyId, settings.SecretAccessKey, config);
}

static bool LooksLikePng(Document document)
{
    var hasPngMimeType = string.Equals(document.MimeType, "image/png", StringComparison.OrdinalIgnoreCase);
    var hasPngExtension = document.FileName?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ?? false;
    return hasPngMimeType || hasPngExtension;
}

static async Task<MemoryStream> DownloadFileAsync(ITelegramBotClient bot, string fileId, CancellationToken cancellationToken)
{
    var file = await bot.GetFile(fileId, cancellationToken);
    if (string.IsNullOrWhiteSpace(file.FilePath))
        throw new InvalidOperationException("Telegram file path was empty.");

    var stream = new MemoryStream();
    await bot.DownloadFile(file.FilePath, stream, cancellationToken);
    stream.Position = 0;
    return stream;
}

static bool HasPngSignature(MemoryStream stream)
{
    var signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };

    if (stream.Length < signature.Length)
    {
        stream.Position = 0;
        return false;
    }

    var header = new byte[signature.Length];
    var bytesRead = stream.Read(header, 0, header.Length);
    stream.Position = 0;

    if (bytesRead != signature.Length)
        return false;

    for (var i = 0; i < signature.Length; i++)
    {
        if (header[i] != signature[i])
            return false;
    }

    return true;
}

static string BuildObjectKey(string objectPrefix, long userId, string? originalFileName)
{
    var safePrefix = string.IsNullOrWhiteSpace(objectPrefix) ? "screenshots" : objectPrefix.Trim().Trim('/');
    var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
    var randomSuffix = Guid.NewGuid().ToString("N")[..8];

    var originalName = Path.GetFileNameWithoutExtension(originalFileName ?? "screenshot");
    if (string.IsNullOrWhiteSpace(originalName))
        originalName = "screenshot";

    var safeName = string.Concat(originalName.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'));
    if (string.IsNullOrWhiteSpace(safeName))
        safeName = "screenshot";

    return $"{safePrefix}/{userId}/{timestamp}-{safeName}-{randomSuffix}.png";
}

static async Task UploadToR2Async(
    IAmazonS3 s3,
    string bucketName,
    string objectKey,
    MemoryStream stream,
    CancellationToken cancellationToken)
{
    stream.Position = 0;
    var request = new PutObjectRequest
    {
        BucketName = bucketName,
        Key = objectKey,
        InputStream = stream,
        ContentType = "image/png",
        AutoCloseStream = false,
        // Cloudflare R2 does not support AWS streaming payload trailers.
        UseChunkEncoding = false,
        DisablePayloadSigning = true
    };

    await s3.PutObjectAsync(request, cancellationToken);
}

internal sealed record BotSettings(
    string BotToken,
    string? WebhookSecret,
    long? AllowedUserId,
    R2Settings R2
);

internal sealed record R2Settings(
    string Endpoint,
    string AccessKeyId,
    string SecretAccessKey,
    string BucketName,
    string ObjectPrefix
);

public partial class Program;
