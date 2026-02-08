using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using TelegramImageBot.Data;
using Telegram.Bot;
using Telegram.Bot.Types;

var builder = WebApplication.CreateBuilder(args);

var settings = LoadSettings(builder.Configuration);
var postgresEnabled = builder.Services.AddSchedulePersistence(builder.Configuration);
var databaseTarget = ResolveDatabaseTarget(builder.Configuration);
var bot = new TelegramBotClient(settings.BotToken);
var s3 = CreateS3Client(settings.R2);

var app = builder.Build();
var logger = app.Logger;

if (databaseTarget is not null)
{
    logger.LogInformation(
        "Database connection source {Source}: Host={Host};Port={Port};Database={Database};Username={Username}",
        databaseTarget.Source,
        databaseTarget.Host,
        databaseTarget.Port,
        databaseTarget.Database,
        databaseTarget.Username
    );
}
else
{
    logger.LogInformation(
        "No PostgreSQL connection string detected in ConnectionStrings:Postgres, DATABASE_URL, or POSTGRES_CONNECTION_STRING.");
}

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
        if (IsStartSessionCommand(message.Text))
        {
            if (!captureSessionsEnabled)
            {
                logger.LogWarning("User {UserId} requested /start_session while capture sessions are disabled.", message.From.Id);
                await bot.SendMessage(
                    chatId.Value,
                    "Capture sessions are unavailable. Configure PostgreSQL and DATABASE_URL first.",
                    cancellationToken: cancellationToken
                );
                return Results.Ok();
            }

            var existingSession = await captureSessionRepository!.GetOpenForUserAsync(message.From.Id, cancellationToken);
            if (existingSession is not null)
            {
                logger.LogInformation(
                    "User {UserId} requested /start_session but session {SessionId} is already open.",
                    message.From.Id,
                    existingSession.Id);
                await bot.SendMessage(
                    chatId.Value,
                    $"A session is already open: {existingSession.Id}. Upload PNG documents, then send /close when done.",
                    cancellationToken: cancellationToken
                );
                return Results.Ok();
            }

            CaptureSessionRecord startedSession;
            try
            {
                startedSession = await captureSessionRepository.CreateAsync(message.From.Id, cancellationToken);
                logger.LogInformation("Opened capture session {SessionId} for user {UserId} via /start_session.",
                    startedSession.Id,
                    message.From.Id);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                startedSession = await captureSessionRepository.GetOpenForUserAsync(message.From.Id, cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Failed to resolve open capture session after unique violation for user {message.From.Id}.");
                logger.LogInformation(
                    "Recovered existing open capture session {SessionId} for user {UserId} after unique violation.",
                    startedSession.Id,
                    message.From.Id);
            }

            await bot.SendMessage(
                chatId.Value,
                $"Opened session {startedSession.Id}. Upload PNG documents and send /close when done.",
                cancellationToken: cancellationToken
            );
            return Results.Ok();
        }

        if (IsCloseSessionCommand(message.Text))
        {
            if (!captureSessionsEnabled)
            {
                logger.LogWarning("User {UserId} requested close command while capture sessions are disabled.", message.From.Id);
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
                logger.LogInformation("User {UserId} requested close command but had no open session.", message.From.Id);
                await bot.SendMessage(
                    chatId.Value,
                    "No open capture session to close.",
                    cancellationToken: cancellationToken
                );
                return Results.Ok();
            }

            var imageCount = await captureImageRepository!.CountBySessionAsync(closedSession.Id, cancellationToken);
            logger.LogInformation("Closed capture session {SessionId} for user {UserId} with {ImageCount} images.",
                closedSession.Id,
                message.From.Id,
                imageCount);
            await bot.SendMessage(
                chatId.Value,
                $"Closed session {closedSession.Id} with {imageCount} image(s).",
                cancellationToken: cancellationToken
            );
            return Results.Ok();
        }

        if (message.Photo?.Any() == true)
        {
            logger.LogInformation("Rejected photo upload for user {UserId}; document upload required.", message.From.Id);
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
        logger.LogInformation(
            "Rejected non-PNG document for user {UserId}. FileName={FileName}; MimeType={MimeType}; MessageId={MessageId}",
            message.From.Id,
            message.Document.FileName,
            message.Document.MimeType,
            message.MessageId);
        await bot.SendMessage(
            chatId.Value,
            "Only PNG files are accepted.",
            cancellationToken: cancellationToken
        );
        return Results.Ok();
    }

    try
    {
        logger.LogInformation(
            "Processing PNG document upload. UpdateId={UpdateId}; UserId={UserId}; ChatId={ChatId}; MessageId={MessageId}; FileName={FileName}; MimeType={MimeType}",
            update.Id,
            message.From.Id,
            chatId.Value,
            message.MessageId,
            message.Document.FileName,
            message.Document.MimeType);

        await using var imageStream = await DownloadFileAsync(bot, message.Document.FileId, cancellationToken);

        if (!HasPngSignature(imageStream))
        {
            logger.LogInformation(
                "Rejected invalid PNG signature for user {UserId}. MessageId={MessageId}; FileName={FileName}",
                message.From.Id,
                message.MessageId,
                message.Document.FileName);
            await bot.SendMessage(
                chatId.Value,
                "The uploaded file is not a valid PNG.",
                cancellationToken: cancellationToken
            );
            return Results.Ok();
        }

        var objectKey = BuildObjectKey(settings.R2.ObjectPrefix, message.From.Id, message.Document.FileName);
        await UploadToR2Async(s3, settings.R2.BucketName, objectKey, imageStream, cancellationToken);
        logger.LogInformation(
            "Uploaded object to R2. UserId={UserId}; MessageId={MessageId}; ObjectKey={ObjectKey}",
            message.From.Id,
            message.MessageId,
            objectKey);

        string sessionInfoSuffix = string.Empty;
        if (captureSessionsEnabled)
        {
            var captureSession = await captureSessionRepository!.GetOpenForUserAsync(message.From.Id, cancellationToken);
            var isImplicitSingleUpload = false;

            if (captureSession is null)
            {
                try
                {
                    captureSession = await captureSessionRepository.CreateAsync(message.From.Id, cancellationToken);
                    isImplicitSingleUpload = true;
                    logger.LogInformation(
                        "Created implicit single-upload capture session {SessionId} for user {UserId}.",
                        captureSession.Id,
                        message.From.Id);
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    captureSession = await captureSessionRepository.GetOpenForUserAsync(message.From.Id, cancellationToken)
                        ?? throw new InvalidOperationException(
                            $"Failed to resolve open capture session after unique violation for user {message.From.Id}.");
                    logger.LogInformation(
                        "Resolved existing open capture session {SessionId} for user {UserId} during upload.",
                        captureSession.Id,
                        message.From.Id);
                }
            }
            else
            {
                logger.LogInformation(
                    "Appending upload to existing open session {SessionId} for user {UserId}.",
                    captureSession.Id,
                    message.From.Id);
            }

            var captureImage = await captureImageRepository!.CreateNextAsync(
                captureSession.Id,
                objectKey,
                message.MessageId,
                cancellationToken
            );
            logger.LogInformation(
                "Persisted capture image {CaptureImageId}. SessionId={SessionId}; Sequence={Sequence}; UserId={UserId}",
                captureImage.Id,
                captureSession.Id,
                captureImage.Sequence,
                message.From.Id);

            if (isImplicitSingleUpload)
            {
                var closedSession = await captureSessionRepository.UpdateStateAsync(
                    captureSession.Id,
                    CaptureSessionState.Closed,
                    cancellationToken: cancellationToken
                );
                if (closedSession is null)
                {
                    logger.LogWarning(
                        "Expected to close implicit single-upload session {SessionId} for user {UserId}, but no row was updated.",
                        captureSession.Id,
                        message.From.Id);
                }
                else
                {
                    logger.LogInformation(
                        "Auto-closed implicit single-upload session {SessionId} for user {UserId}.",
                        captureSession.Id,
                        message.From.Id);
                }

                sessionInfoSuffix =
                    $"\nSession: {captureSession.Id}\nSequence: {captureImage.Sequence}\nSession closed (single-upload mode).";
            }
            else
            {
                sessionInfoSuffix = $"\nSession: {captureSession.Id}\nSequence: {captureImage.Sequence}";
            }
        }
        else
        {
            logger.LogWarning(
                "Capture sessions disabled while processing upload. UserId={UserId}; MessageId={MessageId}; ObjectKey={ObjectKey}",
                message.From.Id,
                message.MessageId,
                objectKey);
        }

        await bot.SendMessage(
            chatId.Value,
            $"Uploaded to R2: {objectKey}{sessionInfoSuffix}",
            cancellationToken: cancellationToken
        );
    }
    catch (Exception ex)
    {
        logger.LogError(
            ex,
            "Failed to process Telegram file update. UpdateId={UpdateId}; UserId={UserId}; ChatId={ChatId}; MessageId={MessageId}",
            update.Id,
            message.From.Id,
            chatId.Value,
            message.MessageId);
        await bot.SendMessage(
            chatId.Value,
            "Upload failed. Check server logs.",
            cancellationToken: cancellationToken
        );
    }

    return Results.Ok();
});

logger.LogInformation("PostgreSQL persistence {State}.", postgresEnabled ? "enabled" : "disabled");
logger.LogInformation("Capture session webhook flow is active (explicit /start_session, /close|/done, implicit single-upload auto-close).");

app.Run();

static DatabaseTarget? ResolveDatabaseTarget(IConfiguration configuration)
{
    var (connectionString, source) = ResolveConfiguredConnectionString(configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
        return null;

    try
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var host = string.IsNullOrWhiteSpace(builder.Host) ? "<empty>" : builder.Host;
        var database = string.IsNullOrWhiteSpace(builder.Database) ? "<empty>" : builder.Database;
        var username = string.IsNullOrWhiteSpace(builder.Username) ? "<empty>" : builder.Username;
        return new DatabaseTarget(
            Source: source,
            Host: host,
            Port: builder.Port,
            Database: database,
            Username: username
        );
    }
    catch
    {
        return new DatabaseTarget(
            Source: source,
            Host: "<unparsed>",
            Port: -1,
            Database: "<unparsed>",
            Username: "<unparsed>"
        );
    }
}

static (string? ConnectionString, string Source) ResolveConfiguredConnectionString(IConfiguration configuration)
{
    var connectionString = configuration.GetConnectionString("Postgres");
    if (!string.IsNullOrWhiteSpace(connectionString))
        return (connectionString, "ConnectionStrings:Postgres");

    connectionString = configuration["DATABASE_URL"];
    if (!string.IsNullOrWhiteSpace(connectionString))
        return (connectionString, "DATABASE_URL");

    connectionString = configuration["POSTGRES_CONNECTION_STRING"];
    if (!string.IsNullOrWhiteSpace(connectionString))
        return (connectionString, "POSTGRES_CONNECTION_STRING");

    return (null, "none");
}

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
        || string.Equals(command, "/done", StringComparison.OrdinalIgnoreCase)
        || command?.StartsWith("/close@", StringComparison.OrdinalIgnoreCase) == true
        || command?.StartsWith("/done@", StringComparison.OrdinalIgnoreCase) == true;
}

static bool IsStartSessionCommand(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
        return false;

    var command = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    return string.Equals(command, "/start_session", StringComparison.OrdinalIgnoreCase)
        || command?.StartsWith("/start_session@", StringComparison.OrdinalIgnoreCase) == true;
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

internal sealed record DatabaseTarget(
    string Source,
    string Host,
    int Port,
    string Database,
    string Username
);

public partial class Program;
