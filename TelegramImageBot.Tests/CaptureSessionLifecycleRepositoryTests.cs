using Npgsql;
using TelegramImageBot.Data;

namespace TelegramImageBot.Tests;

[Collection("PostgresIntegration")]
public sealed class CaptureSessionLifecycleRepositoryTests(PostgresRepositoryFixture fixture)
{
    [RequiresPostgresFact]
    public async Task GetOrCreateOpenForUser_ReturnsSameOpenSession()
    {
        var userId = fixture.NextUserId();
        await fixture.CleanupUserAsync(userId);

        try
        {
            var sessionRepository = new CaptureSessionRepository(GetDataSource());

            var first = await sessionRepository.GetOrCreateOpenForUserAsync(userId);
            var second = await sessionRepository.GetOrCreateOpenForUserAsync(userId);

            Assert.Equal(first.Id, second.Id);
            Assert.Equal(CaptureSessionState.Open, second.State);
        }
        finally
        {
            await fixture.CleanupUserAsync(userId);
        }
    }

    [RequiresPostgresFact]
    public async Task CreateNext_AssignsIncrementingSequenceWithinSession()
    {
        var userId = fixture.NextUserId();
        await fixture.CleanupUserAsync(userId);

        try
        {
            var sessionRepository = new CaptureSessionRepository(GetDataSource());
            var imageRepository = new CaptureImageRepository(GetDataSource());

            var session = await sessionRepository.GetOrCreateOpenForUserAsync(userId);
            var first = await imageRepository.CreateNextAsync(session.Id, $"tests/{Guid.NewGuid():N}-1.png");
            var second = await imageRepository.CreateNextAsync(session.Id, $"tests/{Guid.NewGuid():N}-2.png");
            var count = await imageRepository.CountBySessionAsync(session.Id);

            Assert.Equal(1, first.Sequence);
            Assert.Equal(2, second.Sequence);
            Assert.Equal(2, count);
        }
        finally
        {
            await fixture.CleanupUserAsync(userId);
        }
    }

    [RequiresPostgresFact]
    public async Task MultipleUploads_WhileSessionOpen_GroupIntoSameSession()
    {
        var userId = fixture.NextUserId();
        await fixture.CleanupUserAsync(userId);

        try
        {
            var sessionRepository = new CaptureSessionRepository(GetDataSource());
            var imageRepository = new CaptureImageRepository(GetDataSource());

            var openSession = await sessionRepository.GetOrCreateOpenForUserAsync(userId);
            var first = await imageRepository.CreateNextAsync(openSession.Id, $"tests/{Guid.NewGuid():N}-1.png");
            var second = await imageRepository.CreateNextAsync(openSession.Id, $"tests/{Guid.NewGuid():N}-2.png");
            var third = await imageRepository.CreateNextAsync(openSession.Id, $"tests/{Guid.NewGuid():N}-3.png");
            var persistedOpen = await sessionRepository.GetOpenForUserAsync(userId);

            Assert.Equal(openSession.Id, first.SessionId);
            Assert.Equal(openSession.Id, second.SessionId);
            Assert.Equal(openSession.Id, third.SessionId);
            Assert.Equal(1, first.Sequence);
            Assert.Equal(2, second.Sequence);
            Assert.Equal(3, third.Sequence);
            Assert.NotNull(persistedOpen);
            Assert.Equal(openSession.Id, persistedOpen!.Id);
        }
        finally
        {
            await fixture.CleanupUserAsync(userId);
        }
    }

    [RequiresPostgresFact]
    public async Task CloseOpenForUser_ClosesCurrentAndAllowsNewOpenSession()
    {
        var userId = fixture.NextUserId();
        await fixture.CleanupUserAsync(userId);

        try
        {
            var sessionRepository = new CaptureSessionRepository(GetDataSource());

            var original = await sessionRepository.GetOrCreateOpenForUserAsync(userId);
            var closed = await sessionRepository.CloseOpenForUserAsync(userId);
            var openAfterClose = await sessionRepository.GetOpenForUserAsync(userId);
            var next = await sessionRepository.GetOrCreateOpenForUserAsync(userId);

            Assert.NotNull(closed);
            Assert.Equal(CaptureSessionState.Closed, closed!.State);
            Assert.Null(openAfterClose);
            Assert.NotEqual(original.Id, next.Id);
            Assert.Equal(CaptureSessionState.Open, next.State);
        }
        finally
        {
            await fixture.CleanupUserAsync(userId);
        }
    }

    [RequiresPostgresFact]
    public async Task UploadAfterClose_CreatesNewSessionWithSequenceReset()
    {
        var userId = fixture.NextUserId();
        await fixture.CleanupUserAsync(userId);

        try
        {
            var sessionRepository = new CaptureSessionRepository(GetDataSource());
            var imageRepository = new CaptureImageRepository(GetDataSource());

            var firstSession = await sessionRepository.GetOrCreateOpenForUserAsync(userId);
            var firstImage = await imageRepository.CreateNextAsync(firstSession.Id, $"tests/{Guid.NewGuid():N}-1.png");
            _ = await sessionRepository.CloseOpenForUserAsync(userId);

            var secondSession = await sessionRepository.GetOrCreateOpenForUserAsync(userId);
            var secondImage = await imageRepository.CreateNextAsync(secondSession.Id, $"tests/{Guid.NewGuid():N}-2.png");

            Assert.NotEqual(firstSession.Id, secondSession.Id);
            Assert.Equal(1, firstImage.Sequence);
            Assert.Equal(1, secondImage.Sequence);
            Assert.Equal(firstSession.Id, firstImage.SessionId);
            Assert.Equal(secondSession.Id, secondImage.SessionId);
        }
        finally
        {
            await fixture.CleanupUserAsync(userId);
        }
    }

    [RequiresPostgresFact]
    public async Task CreateNext_WhenSessionClosed_ThrowsDomainGuardError()
    {
        var userId = fixture.NextUserId();
        await fixture.CleanupUserAsync(userId);

        try
        {
            var sessionRepository = new CaptureSessionRepository(GetDataSource());
            var imageRepository = new CaptureImageRepository(GetDataSource());

            var session = await sessionRepository.GetOrCreateOpenForUserAsync(userId);
            _ = await imageRepository.CreateNextAsync(session.Id, $"tests/{Guid.NewGuid():N}-1.png");
            _ = await sessionRepository.CloseOpenForUserAsync(userId);

            var closedInsert = await Assert.ThrowsAsync<PostgresException>(
                () => imageRepository.CreateNextAsync(session.Id, $"tests/{Guid.NewGuid():N}-closed.png"));

            Assert.Equal("P0001", closedInsert.SqlState);
        }
        finally
        {
            await fixture.CleanupUserAsync(userId);
        }
    }

    [RequiresPostgresFact]
    public async Task CloseOpenForUser_WhenNoneOpen_ReturnsNull()
    {
        var userId = fixture.NextUserId();
        await fixture.CleanupUserAsync(userId);

        try
        {
            var sessionRepository = new CaptureSessionRepository(GetDataSource());
            var closed = await sessionRepository.CloseOpenForUserAsync(userId);
            Assert.Null(closed);
        }
        finally
        {
            await fixture.CleanupUserAsync(userId);
        }
    }

    [RequiresPostgresFact]
    public async Task CreateAsync_WhenSecondOpenSessionForSameUser_ThrowsUniqueViolation()
    {
        var userId = fixture.NextUserId();
        await fixture.CleanupUserAsync(userId);

        try
        {
            var sessionRepository = new CaptureSessionRepository(GetDataSource());
            _ = await sessionRepository.CreateAsync(userId);

            var duplicate = await Assert.ThrowsAsync<PostgresException>(() => sessionRepository.CreateAsync(userId));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        }
        finally
        {
            await fixture.CleanupUserAsync(userId);
        }
    }

    private NpgsqlDataSource GetDataSource()
    {
        if (fixture.DataSource is null)
            throw new InvalidOperationException("PostgreSQL data source is not initialized.");

        return fixture.DataSource;
    }
}
