using TelegramImageBot.Data;
using Telegram.Bot;

namespace TelegramImageBot.Workers;

internal sealed class ScheduleNotificationDispatcherService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ScheduleNotificationDispatcherService> logger) : BackgroundService
{
    private readonly bool _enabled = ResolveEnabled(configuration);
    private readonly int _batchSize = ResolveBatchSize(configuration);
    private readonly TimeSpan _pollInterval = ResolvePollInterval(configuration);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            logger.LogInformation("Schedule notification dispatcher is disabled.");
            return;
        }

        logger.LogInformation(
            "Schedule notification dispatcher started. PollIntervalSeconds={PollIntervalSeconds}; BatchSize={BatchSize}.",
            _pollInterval.TotalSeconds,
            _batchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var notificationRepository = scope.ServiceProvider.GetRequiredService<IScheduleNotificationRepository>();
                var bot = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();

                var dispatchResult = await notificationRepository.DispatchPendingAsync(
                    async (notification, token) =>
                    {
                        await bot.SendMessage(
                            notification.UserId,
                            notification.MessageText,
                            cancellationToken: token
                        );
                    },
                    batchSize: _batchSize,
                    cancellationToken: stoppingToken);

                if (dispatchResult.ClaimedCount > 0)
                {
                    logger.LogInformation(
                        "Schedule notification dispatch cycle finished. Claimed={ClaimedCount}; Sent={SentCount}; Failed={FailedCount}.",
                        dispatchResult.ClaimedCount,
                        dispatchResult.SentCount,
                        dispatchResult.FailedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Schedule notification dispatcher iteration failed.");
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
            configuration["ScheduleNotificationDispatcher:Enabled"]
            ?? configuration["SCHEDULE_NOTIFICATION_DISPATCHER_ENABLED"];

        if (string.IsNullOrWhiteSpace(configuredValue))
            return true;

        return bool.TryParse(configuredValue, out var enabled) ? enabled : true;
    }

    private static TimeSpan ResolvePollInterval(IConfiguration configuration)
    {
        var configuredValue =
            configuration["ScheduleNotificationDispatcher:PollIntervalSeconds"]
            ?? configuration["SCHEDULE_NOTIFICATION_DISPATCHER_POLL_INTERVAL_SECONDS"];

        if (!int.TryParse(configuredValue, out var seconds))
            seconds = 3;

        if (seconds < 1)
            seconds = 1;

        return TimeSpan.FromSeconds(seconds);
    }

    private static int ResolveBatchSize(IConfiguration configuration)
    {
        var configuredValue =
            configuration["ScheduleNotificationDispatcher:BatchSize"]
            ?? configuration["SCHEDULE_NOTIFICATION_DISPATCHER_BATCH_SIZE"];

        if (!int.TryParse(configuredValue, out var batchSize))
            batchSize = 20;

        return Math.Clamp(batchSize, 1, 100);
    }
}
