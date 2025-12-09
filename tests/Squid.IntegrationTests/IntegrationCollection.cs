namespace Squid.IntegrationTests;

[CollectionDefinition("Sequential")]
public class IntegrationCollection : ICollectionFixture<IntegrationFixture<IntegrationCollection>>
{
}
