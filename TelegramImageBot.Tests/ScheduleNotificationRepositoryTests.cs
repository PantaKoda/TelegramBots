using Npgsql;
using NpgsqlTypes;
using TelegramImageBot.Data;

namespace TelegramImageBot.Tests;

[Collection("PostgresIntegration")]
public sealed class ScheduleNotificationRepositoryTests(PostgresRepositoryFixture fixture)
{
    [RequiresPostgresFact]
    public async Task DispatchPendingAsync_WhenSendSucceeds_MarksRowsSent()
    {
        var userId = fixture.NextUserId();
        await CleanupNotificationsAsync(userId);

        try
        {
            var firstId = await InsertPendingNotificationAsync(userId, "message one", DateTime.UtcNow.AddSeconds(-2));
            var secondId = await InsertPendingNotificationAsync(userId, "message two", DateTime.UtcNow.AddSeconds(-1));

            var repository = new ScheduleNotificationRepository(GetDataSource());
            var delivered = new List<string>();

            var result = await repository.DispatchPendingAsync((notification, _) =>
            {
                delivered.Add(notification.MessageText);
                return Task.CompletedTask;
            });

            Assert.Equal(2, result.ClaimedCount);
            Assert.Equal(2, result.SentCount);
            Assert.Equal(0, result.FailedCount);
            Assert.Equal(["message one", "message two"], delivered);

            var firstState = await GetNotificationStateAsync(firstId);
            var secondState = await GetNotificationStateAsync(secondId);

            Assert.Equal("sent", firstState.Status);
            Assert.Equal("sent", secondState.Status);
            Assert.NotNull(firstState.SentAt);
            Assert.NotNull(secondState.SentAt);
        }
        finally
        {
            await CleanupNotificationsAsync(userId);
        }
    }

    [RequiresPostgresFact]
    public async Task DispatchPendingAsync_WhenSendFails_MarksRowFailed()
    {
        var userId = fixture.NextUserId();
        await CleanupNotificationsAsync(userId);

        try
        {
            var notificationId = await InsertPendingNotificationAsync(userId, "this will fail");
            var repository = new ScheduleNotificationRepository(GetDataSource());

            var result = await repository.DispatchPendingAsync((_, _) =>
                throw new InvalidOperationException("synthetic send failure"));

            Assert.Equal(1, result.ClaimedCount);
            Assert.Equal(0, result.SentCount);
            Assert.Equal(1, result.FailedCount);

            var state = await GetNotificationStateAsync(notificationId);
            Assert.Equal("failed", state.Status);
            Assert.Null(state.SentAt);
        }
        finally
        {
            await CleanupNotificationsAsync(userId);
        }
    }

    [RequiresPostgresFact]
    public async Task DispatchPendingAsync_WhenConcurrentWorkersRun_DeliversAtMostOnce()
    {
        var userId = fixture.NextUserId();
        await CleanupNotificationsAsync(userId);

        try
        {
            var notificationId = await InsertPendingNotificationAsync(userId, "deliver once");
            var firstRepository = new ScheduleNotificationRepository(GetDataSource());
            var secondRepository = new ScheduleNotificationRepository(GetDataSource());
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sentCounter = 0;

            var firstTask = Task.Run(async () =>
            {
                await gate.Task;
                return await firstRepository.DispatchPendingAsync(async (_, token) =>
                {
                    Interlocked.Increment(ref sentCounter);
                    await Task.Delay(75, token);
                });
            });

            var secondTask = Task.Run(async () =>
            {
                await gate.Task;
                return await secondRepository.DispatchPendingAsync(async (_, token) =>
                {
                    Interlocked.Increment(ref sentCounter);
                    await Task.Delay(75, token);
                });
            });

            gate.SetResult();
            var results = await Task.WhenAll(firstTask, secondTask);

            Assert.Equal(1, sentCounter);
            Assert.Equal(1, results.Sum(result => result.ClaimedCount));
            Assert.Equal(1, results.Sum(result => result.SentCount));
            Assert.Equal(0, results.Sum(result => result.FailedCount));

            var state = await GetNotificationStateAsync(notificationId);
            Assert.Equal("sent", state.Status);
            Assert.NotNull(state.SentAt);
        }
        finally
        {
            await CleanupNotificationsAsync(userId);
        }
    }

    private async Task CleanupNotificationsAsync(long userId)
    {
        const string sql = """
            DELETE FROM schedule_ingest.schedule_notification
            WHERE user_id = @user_id;
            """;

        await using var command = GetDataSource().CreateCommand(sql);
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> InsertPendingNotificationAsync(
        long userId,
        string messageText,
        DateTime? createdAt = null)
    {
        const string sql = """
            INSERT INTO schedule_ingest.schedule_notification (
                notification_id,
                user_id,
                schedule_date,
                source_session_id,
                status,
                notification_type,
                message,
                event_ids,
                created_at
            )
            VALUES (
                @notification_id,
                @user_id,
                @schedule_date,
                @source_session_id,
                'pending',
                'summary',
                @message,
                @event_ids,
                @created_at
            )
            RETURNING notification_id;
            """;

        var notificationId = Guid.NewGuid().ToString("N");
        await using var command = GetDataSource().CreateCommand(sql);
        command.Parameters.AddWithValue("notification_id", notificationId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("schedule_date", DateOnly.FromDateTime((createdAt ?? DateTime.UtcNow).Date));
        command.Parameters.AddWithValue("source_session_id", Guid.NewGuid());
        command.Parameters.AddWithValue("message", messageText);
        command.Parameters.Add("event_ids", NpgsqlDbType.Jsonb).Value = "[]";
        command.Parameters.AddWithValue("created_at", createdAt ?? DateTime.UtcNow);
        var id = await command.ExecuteScalarAsync();

        return id?.ToString() ?? throw new InvalidOperationException("Insert into schedule_notification returned no id.");
    }

    private async Task<(string Status, DateTime? SentAt)> GetNotificationStateAsync(string notificationId)
    {
        const string sql = """
            SELECT status, sent_at
            FROM schedule_ingest.schedule_notification
            WHERE notification_id = @id;
            """;

        await using var command = GetDataSource().CreateCommand(sql);
        command.Parameters.AddWithValue("id", notificationId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"Notification {notificationId} was not found.");

        var statusOrdinal = reader.GetOrdinal("status");
        var sentAtOrdinal = reader.GetOrdinal("sent_at");
        return (
            Status: reader.GetString(statusOrdinal),
            SentAt: reader.IsDBNull(sentAtOrdinal) ? null : reader.GetDateTime(sentAtOrdinal)
        );
    }

    private NpgsqlDataSource GetDataSource()
    {
        if (fixture.DataSource is null)
            throw new InvalidOperationException("PostgreSQL data source is not initialized.");

        return fixture.DataSource;
    }
}
