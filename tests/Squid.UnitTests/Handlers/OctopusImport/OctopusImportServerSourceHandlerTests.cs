using System.Net;
using Mediator.Net.Contracts;
using Squid.Core.Handlers.CommandHandlers.OctopusImport;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Handlers.OctopusImport;

public class OctopusImportServerSourceHandlerTests
{
    private readonly Mock<IOctopusImportServerSourceService> _serverSource = new();

    [Fact]
    public async Task Probe_DelegatesToTheService()
    {
        var probe = new OctopusServerProbeDto { InstallationId = "inst-1" };
        _serverSource.Setup(s => s.ProbeAsync("https://octopus.example.com", "API-KEY", It.IsAny<CancellationToken>())).ReturnsAsync(probe);

        var response = await new ProbeOctopusServerCommandHandler(_serverSource.Object).Handle(Context(new ProbeOctopusServerCommand { SpaceId = 7, ServerUrl = "https://octopus.example.com", ApiKey = "API-KEY" }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.ShouldBeSameAs(probe);
    }

    [Fact]
    public async Task Connect_DelegatesToTheService()
    {
        var session = new OctopusImportSessionDto { SessionId = Guid.NewGuid() };
        _serverSource.Setup(s => s.ConnectAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var response = await new ConnectOctopusImportCommandHandler(_serverSource.Object).Handle(Context(new ConnectOctopusImportCommand { SpaceId = 7 }), CancellationToken.None);

        response.Data.Session.ShouldBeSameAs(session);
    }

    [Fact]
    public async Task Fetch_PassesEveryFieldToTheService()
    {
        var sessionId = Guid.NewGuid();
        var progress = new OctopusHistoryFetchProgressDto { ProjectIndex = 3 };
        _serverSource.Setup(s => s.FetchHistoryAsync(new OctopusHistoryFetchRequest(sessionId, 7, "https://octopus.example.com", "API-KEY", "Spaces-1"), It.IsAny<CancellationToken>())).ReturnsAsync(progress);

        var response = await new FetchOctopusImportHistoryCommandHandler(_serverSource.Object).Handle(Context(new FetchOctopusImportHistoryCommand { SessionId = sessionId, SpaceId = 7, ServerUrl = "https://octopus.example.com", ApiKey = "API-KEY", OctopusSpaceId = "Spaces-1" }), CancellationToken.None);

        response.Data.ShouldBeSameAs(progress);
    }

    [Fact]
    public async Task ConnectAndFetch_WithoutSpaceContext_AreRefused()
    {
        await Should.ThrowAsync<UnauthorizedAccessException>(() => new ConnectOctopusImportCommandHandler(_serverSource.Object).Handle(Context(new ConnectOctopusImportCommand()), CancellationToken.None));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => new FetchOctopusImportHistoryCommandHandler(_serverSource.Object).Handle(Context(new FetchOctopusImportHistoryCommand { SessionId = Guid.NewGuid() }), CancellationToken.None));

        _serverSource.VerifyNoOtherCalls();
    }

    private static IReceiveContext<T> Context<T>(T message) where T : class, ICommand
    {
        var context = new Mock<IReceiveContext<T>>();
        context.SetupGet(c => c.Message).Returns(message);
        return context.Object;
    }
}
