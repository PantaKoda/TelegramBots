using Npgsql;
using TelegramImageBot.Data;

namespace TelegramImageBot.Tests;

[Collection("PostgresIntegration")]
public sealed class CaptureSessionDispatchRepositoryTests(PostgresRepositoryFixture fixture)
{
    [RequiresPostgresFact]
    public async Task ClaimNextClosedForProcessing_ClaimsClosedSessionWithImages()
    {
        var userId = fixture.NextUserId();
        await fixture.CleanupUserAsync(userId);

        try
        {
            var sessionRepository = new CaptureSessionRepository(GetDataSource());
            var imageRepository = new CaptureImageRepository(GetDataSource());

            var openSession = await sessionRepository.GetOrCreateOpenForUserAsync(userId);
            _ = await imageRepository.CreateNextAsync(openSession.Id, $"tests/{Guid.NewGuid():N}-1.png");
            _ = await sessionRepository.CloseOpenForUserAsync(userId);

            var claimed = await sessionRepository.ClaimNextClosedForProcessingAsync();
            var persisted = await sessionRepository.GetByIdAsync(openSession.Id);

            Assert.NotNull(claimed);
            Assert.Equal(openSession.Id, claimed!.Id);
            Assert.Equal(CaptureSessionState.Processing, claimed.State);
            Assert.NotNull(persisted);
            Assert.Equal(CaptureSessionState.Processing, persisted!.State);
        }
        finally
        {
            await fixture.CleanupUserAsync(userId);
        }
    }

    [RequiresPostgresFact]
    public async Task ClaimNextClosedForProcessing_SkipsClosedSessionWithoutImages()
    {
        var userId = fixture.NextUserId();
        await fixture.CleanupUserAsync(userId);

        try
        {
            var sessionRepository = new CaptureSessionRepository(GetDataSource());
            var imageRepository = new CaptureImageRepository(GetDataSource());

            var noImageSession = await sessionRepository.GetOrCreateOpenForUserAsync(userId);
            _ = await sessionRepository.CloseOpenForUserAsync(userId);

            var imageSession = await sessionRepository.GetOrCreateOpenForUserAsync(userId);
            _ = await imageRepository.CreateNextAsync(imageSession.Id, $"tests/{Guid.NewGuid():N}-2.png");
            _ = await sessionRepository.CloseOpenForUserAsync(userId);

            var claimed = await sessionRepository.ClaimNextClosedForProcessingAsync();
            var noImagePersisted = await sessionRepository.GetByIdAsync(noImageSession.Id);
            var imagePersisted = await sessionRepository.GetByIdAsync(imageSession.Id);

            Assert.NotNull(claimed);
            Assert.Equal(imageSession.Id, claimed!.Id);
            Assert.NotNull(noImagePersisted);
            Assert.NotNull(imagePersisted);
            Assert.Equal(CaptureSessionState.Closed, noImagePersisted!.State);
            Assert.Equal(CaptureSessionState.Processing, imagePersisted!.State);
        }
        finally
        {
            await fixture.CleanupUserAsync(userId);
        }
    }

    [RequiresPostgresFact]
    public async Task ClaimNextClosedForProcessing_WhenConcurrentClaims_OnlyOneClaimsSession()
    {
        var userId = fixture.NextUserId();
        await fixture.CleanupUserAsync(userId);

        try
        {
            var setupSessionRepository = new CaptureSessionRepository(GetDataSource());
            var imageRepository = new CaptureImageRepository(GetDataSource());
            var openSession = await setupSessionRepository.GetOrCreateOpenForUserAsync(userId);
            _ = await imageRepository.CreateNextAsync(openSession.Id, $"tests/{Guid.NewGuid():N}-3.png");
            _ = await setupSessionRepository.CloseOpenForUserAsync(userId);

            var firstRepository = new CaptureSessionRepository(GetDataSource());
            var secondRepository = new CaptureSessionRepository(GetDataSource());
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var firstTask = Task.Run(async () =>
            {
                await gate.Task;
                return await firstRepository.ClaimNextClosedForProcessingAsync();
            });

            var secondTask = Task.Run(async () =>
            {
                await gate.Task;
                return await secondRepository.ClaimNextClosedForProcessingAsync();
            });

            gate.SetResult();
            var claims = await Task.WhenAll(firstTask, secondTask);

            Assert.Equal(1, claims.Count(claim => claim is not null));
            Assert.Equal(openSession.Id, claims.Single(claim => claim is not null)!.Id);

            var persisted = await setupSessionRepository.GetByIdAsync(openSession.Id);
            Assert.NotNull(persisted);
            Assert.Equal(CaptureSessionState.Processing, persisted!.State);
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
