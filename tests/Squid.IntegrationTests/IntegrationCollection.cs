using Squid.IntegrationTests.Fixtures;
using Xunit;

namespace Squid.IntegrationTests;

[CollectionDefinition("Sequential")]
public class IntegrationCollection : ICollectionFixture<TestContainer>
{
}
