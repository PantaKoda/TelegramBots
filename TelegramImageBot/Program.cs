using Telegram.Bot;
using Telegram.Bot.Types;

var builder = WebApplication.CreateBuilder(args);

var botToken = builder.Configuration["TELEGRAM_BOT_TOKEN"]
               ?? throw new Exception("Missing TELEGRAM_BOT_TOKEN");

var secret = builder.Configuration["TELEGRAM_WEBHOOK_SECRET"]; // optional
var bot = new TelegramBotClient(botToken);

var app = builder.Build();

app.MapPost("/telegram/hook", async (HttpRequest req, Update update) =>
{
    if (!string.IsNullOrEmpty(secret))
    {
        if (!req.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var got) || got != secret)
            return Results.Unauthorized();
    }

    var chatId = update.Message?.Chat.Id;
    if (chatId is not null)
        await bot.SendMessage(chatId.Value, "OK"); // Telegram.Bot v22 uses SendMessage :contentReference[oaicite:0]{index=0}

    return Results.Ok();
});

app.Run();