using Squid.Core.Handlers.RequestHandlers.Machines;
using Squid.Core.Services.Machines;
using Squid.Message.Requests.Machines;

namespace Squid.UnitTests.Services.Machines;

public class GetConnectionStatusRequestHandlerTests
{
    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
    private readonly GetConnectionStatusRequestHandler _handler;

    public GetConnectionStatusRequestHandlerTests()
    {
        _handler = new GetConnectionStatusRequestHandler(_machineDataProvider.Object);
    }

    private static Mock<IReceiveContext<GetConnectionStatusRequest>> CreateContext(string subscriptionId)
    {
        var context = new Mock<IReceiveContext<GetConnectionStatusRequest>>();
        context.Setup(x => x.Message).Returns(new GetConnectionStatusRequest { SubscriptionId = subscriptionId });
        return context;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_ReturnsConnectedStatus_MatchingProviderResult(bool exists)
    {
        _machineDataProvider
            .Setup(x => x.ExistsBySubscriptionIdAsync("sub-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(exists);

        var result = await _handler.Handle(CreateContext("sub-123").Object, CancellationToken.None);

        result.Data.Connected.ShouldBe(exists);
    }

    [Fact]
    public async Task Handle_PassesSubscriptionId_ToDataProvider()
    {
        _machineDataProvider
            .Setup(x => x.ExistsBySubscriptionIdAsync("specific-sub", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _handler.Handle(CreateContext("specific-sub").Object, CancellationToken.None);

        _machineDataProvider.Verify(
            x => x.ExistsBySubscriptionIdAsync("specific-sub", It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
