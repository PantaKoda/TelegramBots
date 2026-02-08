using Npgsql;

namespace TelegramImageBot.Tests;

public sealed class RequiresPostgresFactAttribute : FactAttribute
{
    public RequiresPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(PostgresTestConfiguration.ConnectionString))
        {
            Skip = "Set TEST_DATABASE_URL or DATABASE_URL to run PostgreSQL integration tests.";
        }
    }
}

internal static class PostgresTestConfiguration
{
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
        ?? Environment.GetEnvironmentVariable("DATABASE_URL");
}

public sealed class PostgresRepositoryFixture : IAsyncLifetime
{
    private static long _userIdSeed = -900_000_000_000_000_000;

    public NpgsqlDataSource? DataSource { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = PostgresTestConfiguration.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        DataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        await ApplySchemaAsync(DataSource);
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
            await DataSource.DisposeAsync();
    }

    public long NextUserId() => Interlocked.Increment(ref _userIdSeed);

    public async Task CleanupUserAsync(long userId)
    {
        if (DataSource is null)
            return;

        const string sql = """
            DELETE FROM schedule_ingest.schedule_version WHERE user_id = @user_id;
            DELETE FROM schedule_ingest.day_schedule WHERE user_id = @user_id;
            DELETE FROM schedule_ingest.capture_session WHERE user_id = @user_id;
            """;

        await using var command = DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ApplySchemaAsync(NpgsqlDataSource dataSource)
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var scripts = new[]
        {
            Path.Combine(repositoryRoot, "database", "001_schedule_ingest_schema.sql"),
            Path.Combine(repositoryRoot, "database", "002_capture_session_single_open_per_user.sql")
        };

        foreach (var scriptPath in scripts)
        {
            var sql = await File.ReadAllTextAsync(scriptPath);
            await using var command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static string ResolveRepositoryRoot()
    {
        var rootFromBase = FindRepositoryRoot(AppContext.BaseDirectory);
        if (rootFromBase is not null)
            return rootFromBase;

        var rootFromCurrent = FindRepositoryRoot(Directory.GetCurrentDirectory());
        if (rootFromCurrent is not null)
            return rootFromCurrent;

        throw new InvalidOperationException(
            $"Could not locate repository root for PostgreSQL test scripts. BaseDirectory={AppContext.BaseDirectory}, CurrentDirectory={Directory.GetCurrentDirectory()}");
    }

    private static string? FindRepositoryRoot(string startPath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startPath));
        while (directory is not null)
        {
            var solutionPath = Path.Combine(directory.FullName, "TelegramBots.sln");
            if (File.Exists(solutionPath))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}
