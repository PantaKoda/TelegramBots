namespace TelegramImageBot.Tests;

[CollectionDefinition("PostgresIntegration", DisableParallelization = true)]
public sealed class PostgresIntegrationCollection : ICollectionFixture<PostgresRepositoryFixture>
{
}
