using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Handlers.RequestHandlers.Machines;
using Squid.Core.Services.Machines;
using Squid.Message.Requests.Machines;

namespace Squid.UnitTests.Services.Machines;

/// <summary>
/// Pins the connection-log handler: it clamps the requested entry cap to a sane
/// range and delegates to the reader with the route machine id.
/// </summary>
public sealed class GetMachineConnectionLogRequestHandlerTests
{
    private readonly Mock<IMachineConnectionLogReader> _reader = new();
    private int _capturedMax = -1;

    private GetMachineConnectionLogRequestHandler Build()
    {
        _reader.Setup(r => r.ReadAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<int, int, CancellationToken>((_, max, _) => _capturedMax = max)
            .ReturnsAsync(new GetMachineConnectionLogResponseData());
        return new GetMachineConnectionLogRequestHandler(_reader.Object);
    }

    private static Mock<IReceiveContext<GetMachineConnectionLogRequest>> Context(int machineId, int? maxEntries)
    {
        var ctx = new Mock<IReceiveContext<GetMachineConnectionLogRequest>>();
        ctx.Setup(x => x.Message).Returns(new GetMachineConnectionLogRequest { MachineId = machineId, MaxEntries = maxEntries });
        return ctx;
    }

    [Theory]
    [InlineData(null, GetMachineConnectionLogRequest.DefaultMaxEntries)]   // unset → default
    [InlineData(0, 1)]                                                     // below floor → 1
    [InlineData(-10, 1)]                                                   // negative → 1
    [InlineData(50, 50)]                                                   // in range → passthrough
    [InlineData(99999, GetMachineConnectionLogRequest.MaxAllowedEntries)]  // above ceiling → cap
    public async Task Handle_ClampsMaxEntries(int? requested, int expected)
    {
        var handler = Build();

        await handler.Handle(Context(7, requested).Object, CancellationToken.None);

        _capturedMax.ShouldBe(expected);
        _reader.Verify(r => r.ReadAsync(7, expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WrapsReaderDataInResponse()
    {
        _reader.Setup(r => r.ReadAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetMachineConnectionLogResponseData { MachineId = 7, Endpoint = "poll://s/" });
        var handler = new GetMachineConnectionLogRequestHandler(_reader.Object);

        var response = await handler.Handle(Context(7, null).Object, CancellationToken.None);

        response.Data.ShouldNotBeNull();
        response.Data.MachineId.ShouldBe(7);
        response.Data.Endpoint.ShouldBe("poll://s/");
    }
}
