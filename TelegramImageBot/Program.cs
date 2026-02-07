using Amazon.S3;
using Amazon.S3.Model;
using System.Diagnostics.CodeAnalysis;
using Telegram.Bot;
using Telegram.Bot.Types;

var builder = WebApplication.CreateBuilder(args);

var settings = LoadSettings(builder.Configuration);
var bot = new TelegramBotClient(settings.BotToken);
var s3 = CreateS3Client(settings.R2);

var app = builder.Build();
var logger = app.Logger;

app.MapGet("/", () => Results.Ok("Telegram image bot is running."));

app.MapPost("/telegram/hook", async Task<IResult> (HttpRequest req, Update update, CancellationToken cancellationToken) =>
{
    if (!IsSecretValid(req, settings.WebhookSecret))
        return Results.Unauthorized();

    var message = update.Message;
    var chatId = message?.Chat.Id;
    if (message is null || chatId is null || message.From is null)
        return Results.Ok();

    if (settings.AllowedUserIds is not null && !settings.AllowedUserIds.Contains(message.From.Id))
        return Results.Ok();

    if (!TryGetSupportedUpload(message, out var upload, out var rejectionMessage))
    {
        if (!string.IsNullOrWhiteSpace(rejectionMessage))
        {
            await bot.SendMessage(
                chatId.Value,
                rejectionMessage,
                cancellationToken: cancellationToken
            );
        }

        return Results.Ok();
    }

    try
    {
        await using var mediaStream = await DownloadFileAsync(bot, upload.FileId, cancellationToken);
        var objectKey = BuildObjectKey(
            settings.R2.ObjectPrefix,
            message.From.Id,
            upload.Category,
            upload.FileName,
            upload.FallbackBaseName,
            upload.Extension
        );
        await UploadToR2Async(s3, settings.R2.BucketName, objectKey, upload.ContentType, mediaStream, cancellationToken);

        await bot.SendMessage(
            chatId.Value,
            $"Uploaded to R2: {objectKey}",
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

app.Run();

static BotSettings LoadSettings(IConfiguration configuration)
{
    var botToken = RequireSetting(configuration, "TELEGRAM_BOT_TOKEN");
    var webhookSecret = configuration["TELEGRAM_WEBHOOK_SECRET"];
    var allowedUserIds = ParseAllowedUserIds(configuration);

    var r2 = new R2Settings(
        Endpoint: RequireSetting(configuration, "R2_ENDPOINT"),
        AccessKeyId: RequireSetting(configuration, "R2_ACCESS_KEY_ID"),
        SecretAccessKey: RequireSetting(configuration, "R2_SECRET_ACCESS_KEY"),
        BucketName: RequireSetting(configuration, "R2_BUCKET_NAME"),
        ObjectPrefix: configuration["R2_OBJECT_PREFIX"] ?? "family-media"
    );

    return new BotSettings(botToken, webhookSecret, allowedUserIds, r2);
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

static HashSet<long>? ParseAllowedUserIds(IConfiguration configuration)
{
    var rawValues = new List<string>();

    var allowedUserIdsRaw = configuration["TELEGRAM_ALLOWED_USER_IDS"];
    if (!string.IsNullOrWhiteSpace(allowedUserIdsRaw))
    {
        rawValues.AddRange(allowedUserIdsRaw.Split(
            [',', ';', ' ', '\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        ));
    }

    var singleAllowedUserId = configuration["TELEGRAM_ALLOWED_USER_ID"];
    if (!string.IsNullOrWhiteSpace(singleAllowedUserId))
        rawValues.Add(singleAllowedUserId.Trim());

    if (rawValues.Count == 0)
        return null;

    var parsedIds = new HashSet<long>();
    foreach (var rawValue in rawValues)
    {
        if (!long.TryParse(rawValue, out var parsedId))
            throw new InvalidOperationException("TELEGRAM_ALLOWED_USER_IDS and TELEGRAM_ALLOWED_USER_ID must contain valid integers.");

        parsedIds.Add(parsedId);
    }

    return parsedIds;
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

static bool TryGetSupportedUpload(Message message, [NotNullWhen(true)] out IncomingUpload? upload, out string? rejectionMessage)
{
    upload = null;
    rejectionMessage = null;

    if (message.Video is not null)
    {
        upload = CreateUploadFromVideo(message.Video);
        return true;
    }

    if (message.Photo?.Any() == true)
    {
        upload = CreateUploadFromPhoto(message.Photo);
        return true;
    }

    if (message.Document is null)
        return false;

    if (TryCreateUploadFromDocument(message.Document, out upload))
        return true;

    rejectionMessage = "Only image and video files are accepted.";
    return false;
}

static IncomingUpload CreateUploadFromPhoto(IEnumerable<PhotoSize> photoSizes)
{
    var preferredPhoto = photoSizes
        .OrderByDescending(photo => photo.FileSize ?? 0)
        .ThenByDescending(photo => photo.Width * photo.Height)
        .First();

    return new IncomingUpload(
        FileId: preferredPhoto.FileId,
        FileName: null,
        ContentType: "image/jpeg",
        Extension: ".jpg",
        FallbackBaseName: "photo",
        Category: "images"
    );
}

static IncomingUpload CreateUploadFromVideo(Video video)
{
    var extension = ResolveExtension(video.FileName, video.MimeType, ".mp4");
    var contentType = ResolveContentType(video.MimeType, extension, "video/mp4");

    return new IncomingUpload(
        FileId: video.FileId,
        FileName: video.FileName,
        ContentType: contentType,
        Extension: extension,
        FallbackBaseName: "video",
        Category: "videos"
    );
}

static bool TryCreateUploadFromDocument(Document document, [NotNullWhen(true)] out IncomingUpload? upload)
{
    upload = null;
    var fileExtension = Path.GetExtension(document.FileName ?? string.Empty);

    if (LooksLikeImage(document.MimeType, fileExtension))
    {
        var extension = ResolveExtension(document.FileName, document.MimeType, ".jpg");
        var contentType = ResolveContentType(document.MimeType, extension, "image/jpeg");
        upload = new IncomingUpload(
            FileId: document.FileId,
            FileName: document.FileName,
            ContentType: contentType,
            Extension: extension,
            FallbackBaseName: "image",
            Category: "images"
        );
        return true;
    }

    if (LooksLikeVideo(document.MimeType, fileExtension))
    {
        var extension = ResolveExtension(document.FileName, document.MimeType, ".mp4");
        var contentType = ResolveContentType(document.MimeType, extension, "video/mp4");
        upload = new IncomingUpload(
            FileId: document.FileId,
            FileName: document.FileName,
            ContentType: contentType,
            Extension: extension,
            FallbackBaseName: "video",
            Category: "videos"
        );
        return true;
    }

    return false;
}

static bool LooksLikeImage(string? mimeType, string? extension)
{
    if (!string.IsNullOrWhiteSpace(mimeType)
        && mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return IsKnownImageExtension(extension);
}

static bool LooksLikeVideo(string? mimeType, string? extension)
{
    if (!string.IsNullOrWhiteSpace(mimeType)
        && mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return IsKnownVideoExtension(extension);
}

static bool IsKnownImageExtension(string? extension)
{
    return NormalizeExtension(extension) switch
    {
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".heic" or ".heif" => true,
        _ => false
    };
}

static bool IsKnownVideoExtension(string? extension)
{
    return NormalizeExtension(extension) switch
    {
        ".mp4" or ".mov" or ".m4v" or ".webm" or ".mkv" or ".avi" or ".3gp" => true,
        _ => false
    };
}

static string ResolveExtension(string? fileName, string? mimeType, string fallbackExtension)
{
    var fromFileName = NormalizeExtension(Path.GetExtension(fileName ?? string.Empty));
    if (!string.IsNullOrWhiteSpace(fromFileName))
        return fromFileName;

    var fromMimeType = ExtensionFromMimeType(mimeType);
    if (!string.IsNullOrWhiteSpace(fromMimeType))
        return fromMimeType;

    return NormalizeExtension(fallbackExtension) ?? ".bin";
}

static string ResolveContentType(string? mimeType, string extension, string fallbackContentType)
{
    if (!string.IsNullOrWhiteSpace(mimeType))
        return mimeType;

    return ContentTypeFromExtension(extension) ?? fallbackContentType;
}

static string? NormalizeExtension(string? extension)
{
    if (string.IsNullOrWhiteSpace(extension))
        return null;

    var raw = extension.Trim();
    if (raw.StartsWith('.'))
        raw = raw[1..];

    raw = string.Concat(raw.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(raw))
        return null;

    return $".{raw}";
}

static string? ExtensionFromMimeType(string? mimeType)
{
    return mimeType?.Trim().ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tiff",
        "image/heic" => ".heic",
        "image/heif" => ".heif",
        "video/mp4" => ".mp4",
        "video/quicktime" => ".mov",
        "video/webm" => ".webm",
        "video/x-matroska" => ".mkv",
        "video/3gpp" => ".3gp",
        _ => null
    };
}

static string? ContentTypeFromExtension(string extension)
{
    return NormalizeExtension(extension) switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".heic" => "image/heic",
        ".heif" => "image/heif",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".m4v" => "video/x-m4v",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".3gp" => "video/3gpp",
        _ => null
    };
}

static string BuildObjectKey(
    string objectPrefix,
    long userId,
    string category,
    string? originalFileName,
    string fallbackBaseName,
    string extension)
{
    var safePrefix = string.IsNullOrWhiteSpace(objectPrefix) ? "family-media" : objectPrefix.Trim().Trim('/');
    var safeCategory = string.Concat(category.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'));
    if (string.IsNullOrWhiteSpace(safeCategory))
        safeCategory = "files";

    var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
    var randomSuffix = Guid.NewGuid().ToString("N")[..8];

    var originalName = Path.GetFileNameWithoutExtension(originalFileName ?? fallbackBaseName);
    if (string.IsNullOrWhiteSpace(originalName))
        originalName = fallbackBaseName;

    var safeName = string.Concat(originalName.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'));
    if (string.IsNullOrWhiteSpace(safeName))
        safeName = fallbackBaseName;

    var safeExtension = NormalizeExtension(extension) ?? ".bin";

    return $"{safePrefix}/{userId}/{safeCategory}/{timestamp}-{safeName}-{randomSuffix}{safeExtension}";
}

static async Task UploadToR2Async(
    IAmazonS3 s3,
    string bucketName,
    string objectKey,
    string contentType,
    MemoryStream stream,
    CancellationToken cancellationToken)
{
    stream.Position = 0;
    var request = new PutObjectRequest
    {
        BucketName = bucketName,
        Key = objectKey,
        InputStream = stream,
        ContentType = contentType,
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
    IReadOnlySet<long>? AllowedUserIds,
    R2Settings R2
);

internal sealed record R2Settings(
    string Endpoint,
    string AccessKeyId,
    string SecretAccessKey,
    string BucketName,
    string ObjectPrefix
);

internal sealed record IncomingUpload(
    string FileId,
    string? FileName,
    string ContentType,
    string Extension,
    string FallbackBaseName,
    string Category
);

public partial class Program;

