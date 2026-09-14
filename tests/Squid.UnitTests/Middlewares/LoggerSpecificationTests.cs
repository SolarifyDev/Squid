using System.IO;
using System.Linq;
using Mediator.Net.Contracts;
using Mediator.Net.Context;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Squid.Core.Middlewares.Logging;
using Squid.Message.Commands.OctopusImport;

namespace Squid.UnitTests.Middlewares;

public class LoggerSpecificationTests
{
    [Fact]
    public async Task BeforeExecute_WhenUploadingOctopusArchive_DoesNotLogPasswordOrContent()
    {
        const string password = "octopus-import-password";
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var context = new Mock<IContext<IMessage>>();
        context.SetupGet(c => c.Message).Returns(new UploadOctopusImportCommand
        {
            SpaceId = 7,
            Password = password,
            FileName = "export.zip",
            ContentType = "application/zip",
            SizeBytes = 123,
            Content = new MemoryStream([1, 2, 3])
        });
        var sut = new LoggerSpecification<IContext<IMessage>>(logger);

        await sut.BeforeExecute(context.Object, CancellationToken.None);

        var rendered = sink.Events.Single().RenderMessage();
        rendered.ShouldNotContain(password);
        rendered.ShouldNotContain("Password", Case.Insensitive);
        rendered.ShouldNotContain("Content:", Case.Insensitive);
        rendered.ShouldContain("export.zip");
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
