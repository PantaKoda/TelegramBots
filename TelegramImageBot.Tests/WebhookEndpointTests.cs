using System.Net;
using System.Net.Http.Json;

namespace TelegramImageBot.Tests;

public sealed class WebhookEndpointTests : IClassFixture<TelegramImageBotFactory>
{
    private readonly HttpClient _client;

    public WebhookEndpointTests(TelegramImageBotFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Root_ReturnsOk()
    {
        var response = await _client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Telegram image bot is running.", body);
    }

    [Fact]
    public async Task Webhook_WithoutSecretHeader_ReturnsUnauthorized()
    {
        var response = await _client.PostAsJsonAsync("/telegram/hook", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
