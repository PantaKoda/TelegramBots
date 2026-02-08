using Npgsql;
using NpgsqlTypes;

namespace TelegramImageBot.Data;

internal static class SchedulePersistenceServiceCollectionExtensions
{
    public static bool AddSchedulePersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = ResolveConnectionString(configuration);
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        services.AddSingleton(dataSource);

        services.AddScoped<ICaptureSessionRepository, CaptureSessionRepository>();
        services.AddScoped<ICaptureImageRepository, CaptureImageRepository>();
        services.AddScoped<IDayScheduleRepository, DayScheduleRepository>();
        services.AddScoped<IScheduleVersionRepository, ScheduleVersionRepository>();

        return true;
    }

    private static string? ResolveConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (!string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        connectionString = configuration["DATABASE_URL"];
        if (!string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        return configuration["POSTGRES_CONNECTION_STRING"];
    }
}

internal enum CaptureSessionState
{
    Open,
    Closed,
    Processing,
    Done,
    Failed
}

internal static class CaptureSessionStateMapper
{
    public static string ToDatabaseValue(this CaptureSessionState state)
    {
        return state switch
        {
            CaptureSessionState.Open => "open",
            CaptureSessionState.Closed => "closed",
            CaptureSessionState.Processing => "processing",
            CaptureSessionState.Done => "done",
            CaptureSessionState.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported capture session state.")
        };
    }

    public static CaptureSessionState FromDatabaseValue(string state)
    {
        return state switch
        {
            "open" => CaptureSessionState.Open,
            "closed" => CaptureSessionState.Closed,
            "processing" => CaptureSessionState.Processing,
            "done" => CaptureSessionState.Done,
            "failed" => CaptureSessionState.Failed,
            _ => throw new InvalidOperationException($"Unknown capture session state from database: {state}")
        };
    }
}

internal sealed record CaptureSessionRecord(
    Guid Id,
    long UserId,
    CaptureSessionState State,
    DateTime CreatedAt,
    DateTime? ClosedAt,
    string? Error
);

internal sealed record CaptureImageRecord(
    Guid Id,
    Guid SessionId,
    int Sequence,
    string R2Key,
    long? TelegramMessageId,
    DateTime CreatedAt
);

internal sealed record DayScheduleRecord(
    long UserId,
    DateOnly ScheduleDate,
    int CurrentVersion
);

internal sealed record ScheduleVersionRecord(
    long UserId,
    DateOnly ScheduleDate,
    int Version,
    Guid SessionId,
    string PayloadJson,
    string PayloadHash,
    DateTime CreatedAt
);

internal interface ICaptureSessionRepository
{
    Task<CaptureSessionRecord> CreateAsync(long userId, CancellationToken cancellationToken = default);
    Task<CaptureSessionRecord?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<CaptureSessionRecord?> UpdateStateAsync(
        Guid sessionId,
        CaptureSessionState state,
        string? error = null,
        CancellationToken cancellationToken = default);
}

internal interface ICaptureImageRepository
{
    Task<CaptureImageRecord> CreateAsync(
        Guid sessionId,
        int sequence,
        string r2Key,
        long? telegramMessageId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CaptureImageRecord>> ListBySessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

internal interface IDayScheduleRepository
{
    Task<DayScheduleRecord?> GetAsync(long userId, DateOnly scheduleDate, CancellationToken cancellationToken = default);
}

internal interface IScheduleVersionRepository
{
    Task<ScheduleVersionRecord> CreateAsync(
        long userId,
        DateOnly scheduleDate,
        int version,
        Guid sessionId,
        string payloadJson,
        string payloadHash,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduleVersionRecord>> ListByDayAsync(
        long userId,
        DateOnly scheduleDate,
        CancellationToken cancellationToken = default);
}

internal sealed class CaptureSessionRepository(NpgsqlDataSource dataSource) : ICaptureSessionRepository
{
    public async Task<CaptureSessionRecord> CreateAsync(long userId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO schedule_ingest.capture_session (user_id)
            VALUES (@user_id)
            RETURNING id, user_id, state, created_at, closed_at, error;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("user_id", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Insert into capture_session returned no rows.");

        return MapCaptureSession(reader);
    }

    public async Task<CaptureSessionRecord?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, user_id, state, created_at, closed_at, error
            FROM schedule_ingest.capture_session
            WHERE id = @id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return MapCaptureSession(reader);
    }

    public async Task<CaptureSessionRecord?> UpdateStateAsync(
        Guid sessionId,
        CaptureSessionState state,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE schedule_ingest.capture_session
            SET state = @state::schedule_ingest.capture_session_state,
                error = @error
            WHERE id = @id
            RETURNING id, user_id, state, created_at, closed_at, error;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("state", state.ToDatabaseValue());
        var errorParameter = command.Parameters.Add("error", NpgsqlDbType.Text);
        errorParameter.Value = (object?)error ?? DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return MapCaptureSession(reader);
    }

    private static CaptureSessionRecord MapCaptureSession(NpgsqlDataReader reader)
    {
        var idOrdinal = reader.GetOrdinal("id");
        var userIdOrdinal = reader.GetOrdinal("user_id");
        var stateOrdinal = reader.GetOrdinal("state");
        var createdAtOrdinal = reader.GetOrdinal("created_at");
        var closedAtOrdinal = reader.GetOrdinal("closed_at");
        var errorOrdinal = reader.GetOrdinal("error");

        return new CaptureSessionRecord(
            Id: reader.GetGuid(idOrdinal),
            UserId: reader.GetInt64(userIdOrdinal),
            State: CaptureSessionStateMapper.FromDatabaseValue(reader.GetString(stateOrdinal)),
            CreatedAt: reader.GetDateTime(createdAtOrdinal),
            ClosedAt: reader.IsDBNull(closedAtOrdinal) ? null : reader.GetDateTime(closedAtOrdinal),
            Error: reader.IsDBNull(errorOrdinal) ? null : reader.GetString(errorOrdinal)
        );
    }
}

internal sealed class CaptureImageRepository(NpgsqlDataSource dataSource) : ICaptureImageRepository
{
    public async Task<CaptureImageRecord> CreateAsync(
        Guid sessionId,
        int sequence,
        string r2Key,
        long? telegramMessageId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(r2Key))
            throw new ArgumentException("R2 key cannot be empty.", nameof(r2Key));

        const string sql = """
            INSERT INTO schedule_ingest.capture_image (session_id, sequence, r2_key, telegram_message_id)
            VALUES (@session_id, @sequence, @r2_key, @telegram_message_id)
            RETURNING id, session_id, sequence, r2_key, telegram_message_id, created_at;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("session_id", sessionId);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("r2_key", r2Key);
        var messageIdParameter = command.Parameters.Add("telegram_message_id", NpgsqlDbType.Bigint);
        messageIdParameter.Value = (object?)telegramMessageId ?? DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Insert into capture_image returned no rows.");

        return MapCaptureImage(reader);
    }

    public async Task<IReadOnlyList<CaptureImageRecord>> ListBySessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, session_id, sequence, r2_key, telegram_message_id, created_at
            FROM schedule_ingest.capture_image
            WHERE session_id = @session_id
            ORDER BY sequence;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("session_id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var images = new List<CaptureImageRecord>();

        while (await reader.ReadAsync(cancellationToken))
        {
            images.Add(MapCaptureImage(reader));
        }

        return images;
    }

    private static CaptureImageRecord MapCaptureImage(NpgsqlDataReader reader)
    {
        var idOrdinal = reader.GetOrdinal("id");
        var sessionIdOrdinal = reader.GetOrdinal("session_id");
        var sequenceOrdinal = reader.GetOrdinal("sequence");
        var r2KeyOrdinal = reader.GetOrdinal("r2_key");
        var messageIdOrdinal = reader.GetOrdinal("telegram_message_id");
        var createdAtOrdinal = reader.GetOrdinal("created_at");

        return new CaptureImageRecord(
            Id: reader.GetGuid(idOrdinal),
            SessionId: reader.GetGuid(sessionIdOrdinal),
            Sequence: reader.GetInt32(sequenceOrdinal),
            R2Key: reader.GetString(r2KeyOrdinal),
            TelegramMessageId: reader.IsDBNull(messageIdOrdinal) ? null : reader.GetInt64(messageIdOrdinal),
            CreatedAt: reader.GetDateTime(createdAtOrdinal)
        );
    }
}

internal sealed class DayScheduleRepository(NpgsqlDataSource dataSource) : IDayScheduleRepository
{
    public async Task<DayScheduleRecord?> GetAsync(long userId, DateOnly scheduleDate, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT user_id, schedule_date, current_version
            FROM schedule_ingest.day_schedule
            WHERE user_id = @user_id
              AND schedule_date = @schedule_date;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("schedule_date", scheduleDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var userIdOrdinal = reader.GetOrdinal("user_id");
        var scheduleDateOrdinal = reader.GetOrdinal("schedule_date");
        var currentVersionOrdinal = reader.GetOrdinal("current_version");

        return new DayScheduleRecord(
            UserId: reader.GetInt64(userIdOrdinal),
            ScheduleDate: reader.GetFieldValue<DateOnly>(scheduleDateOrdinal),
            CurrentVersion: reader.GetInt32(currentVersionOrdinal)
        );
    }
}

internal sealed class ScheduleVersionRepository(NpgsqlDataSource dataSource) : IScheduleVersionRepository
{
    public async Task<ScheduleVersionRecord> CreateAsync(
        long userId,
        DateOnly scheduleDate,
        int version,
        Guid sessionId,
        string payloadJson,
        string payloadHash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            throw new ArgumentException("Payload JSON cannot be empty.", nameof(payloadJson));

        if (string.IsNullOrWhiteSpace(payloadHash))
            throw new ArgumentException("Payload hash cannot be empty.", nameof(payloadHash));

        const string sql = """
            INSERT INTO schedule_ingest.schedule_version (
                user_id, schedule_date, version, session_id, payload, payload_hash
            )
            VALUES (
                @user_id, @schedule_date, @version, @session_id, @payload, @payload_hash
            )
            RETURNING user_id, schedule_date, version, session_id, payload::text, payload_hash, created_at;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("schedule_date", scheduleDate);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("session_id", sessionId);
        var payloadParameter = command.Parameters.Add("payload", NpgsqlDbType.Jsonb);
        payloadParameter.Value = payloadJson;
        command.Parameters.AddWithValue("payload_hash", payloadHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Insert into schedule_version returned no rows.");

        return MapScheduleVersion(reader);
    }

    public async Task<IReadOnlyList<ScheduleVersionRecord>> ListByDayAsync(
        long userId,
        DateOnly scheduleDate,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT user_id, schedule_date, version, session_id, payload::text, payload_hash, created_at
            FROM schedule_ingest.schedule_version
            WHERE user_id = @user_id
              AND schedule_date = @schedule_date
            ORDER BY version DESC;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("schedule_date", scheduleDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var versions = new List<ScheduleVersionRecord>();

        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(MapScheduleVersion(reader));
        }

        return versions;
    }

    private static ScheduleVersionRecord MapScheduleVersion(NpgsqlDataReader reader)
    {
        var userIdOrdinal = reader.GetOrdinal("user_id");
        var scheduleDateOrdinal = reader.GetOrdinal("schedule_date");
        var versionOrdinal = reader.GetOrdinal("version");
        var sessionIdOrdinal = reader.GetOrdinal("session_id");
        var payloadOrdinal = reader.GetOrdinal("payload");
        var payloadHashOrdinal = reader.GetOrdinal("payload_hash");
        var createdAtOrdinal = reader.GetOrdinal("created_at");

        return new ScheduleVersionRecord(
            UserId: reader.GetInt64(userIdOrdinal),
            ScheduleDate: reader.GetFieldValue<DateOnly>(scheduleDateOrdinal),
            Version: reader.GetInt32(versionOrdinal),
            SessionId: reader.GetGuid(sessionIdOrdinal),
            PayloadJson: reader.GetString(payloadOrdinal),
            PayloadHash: reader.GetString(payloadHashOrdinal),
            CreatedAt: reader.GetDateTime(createdAtOrdinal)
        );
    }
}
