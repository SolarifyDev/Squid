namespace Squid.IntegrationTests.Base;

[CollectionDefinition("Sequential")]
public class IntegrationCollection : ICollectionFixture<IntegrationFixture<IntegrationCollection>>
{
}
