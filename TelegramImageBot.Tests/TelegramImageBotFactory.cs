using Microsoft.AspNetCore.Mvc.Testing;

namespace TelegramImageBot.Tests;

public sealed class TelegramImageBotFactory : WebApplicationFactory<Program>
{
    public TelegramImageBotFactory()
    {
        Environment.SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", "123456:ABCDEF_test_token_value");
        Environment.SetEnvironmentVariable("TELEGRAM_WEBHOOK_SECRET", "test-secret");
        Environment.SetEnvironmentVariable("R2_ENDPOINT", "https://example.r2.cloudflarestorage.com");
        Environment.SetEnvironmentVariable("R2_ACCESS_KEY_ID", "test-access-key");
        Environment.SetEnvironmentVariable("R2_SECRET_ACCESS_KEY", "test-secret-key");
        Environment.SetEnvironmentVariable("R2_BUCKET_NAME", "test-bucket");
        Environment.SetEnvironmentVariable("R2_OBJECT_PREFIX", "screenshots");
    }
}
