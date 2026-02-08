using TelegramImageBot.Data;

namespace TelegramImageBot.Workers;

internal sealed class CaptureSessionDispatcherService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<CaptureSessionDispatcherService> logger) : BackgroundService
{
    private readonly bool _enabled = ResolveEnabled(configuration);
    private readonly TimeSpan _pollInterval = ResolvePollInterval(configuration);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            logger.LogInformation("Capture session dispatcher is disabled.");
            return;
        }

        logger.LogInformation(
            "Capture session dispatcher started. PollIntervalSeconds={PollIntervalSeconds}.",
            _pollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sessionRepository = scope.ServiceProvider.GetRequiredService<ICaptureSessionRepository>();
                var claimedSession = await sessionRepository.ClaimNextClosedForProcessingAsync(stoppingToken);

                if (claimedSession is not null)
                {
                    logger.LogInformation(
                        "Claimed capture session {SessionId} for OCR processing. UserId={UserId}; State={State}.",
                        claimedSession.Id,
                        claimedSession.UserId,
                        claimedSession.State);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Capture session dispatcher iteration failed.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static bool ResolveEnabled(IConfiguration configuration)
    {
        var configuredValue =
            configuration["CaptureSessionDispatcher:Enabled"]
            ?? configuration["CAPTURE_SESSION_DISPATCHER_ENABLED"];

        if (string.IsNullOrWhiteSpace(configuredValue))
            return true;

        return bool.TryParse(configuredValue, out var enabled) ? enabled : true;
    }

    private static TimeSpan ResolvePollInterval(IConfiguration configuration)
    {
        var configuredValue =
            configuration["CaptureSessionDispatcher:PollIntervalSeconds"]
            ?? configuration["CAPTURE_SESSION_DISPATCHER_POLL_INTERVAL_SECONDS"];

        if (!int.TryParse(configuredValue, out var seconds))
            seconds = 5;

        if (seconds < 1)
            seconds = 1;

        return TimeSpan.FromSeconds(seconds);
    }
}
