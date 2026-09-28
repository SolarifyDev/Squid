using System.IO;
using System.Linq;
using Mediator.Net.Contracts;
using Mediator.Net.Context;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Squid.Core.Middlewares.Logging;
using Squid.Message.Commands.Account;
using Squid.Message.Commands.Deployments.Certificate;
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

    [Fact]
    public async Task BeforeExecute_WhenCreatingCertificate_RedactsCertificateDataAndPassword()
    {
        const string certificateData = "base64-certificate-data";
        const string password = "certificate-password";
        var (sink, context, sut) = CreateHarness(new CreateCertificateCommand
        {
            Name = "Production Certificate",
            SpaceId = 7,
            CertificateData = certificateData,
            Password = password
        });

        await sut.BeforeExecute(context.Object, CancellationToken.None);

        var rendered = sink.Events.Single().RenderMessage();
        rendered.ShouldContain("Production Certificate");
        rendered.ShouldNotContain(certificateData);
        rendered.ShouldNotContain(password);
    }

    [Fact]
    public async Task AfterExecute_WhenResponseContainsApiKey_RedactsApiKey()
    {
        const string apiKey = "api-key-secret-value";
        var (sink, context, sut) = CreateHarness(new CreateApiKeyCommand { Description = "CI" });
        context.SetupGet(c => c.Result).Returns(new CreateApiKeyResponse
        {
            Data = new CreateApiKeyResponseData
            {
                Id = 1,
                ApiKey = apiKey,
                Description = "CI"
            }
        });

        await sut.AfterExecute(context.Object, CancellationToken.None);

        var rendered = sink.Events.Single().RenderMessage();
        rendered.ShouldContain("CI");
        rendered.ShouldNotContain(apiKey);
    }

    private static (CollectingSink Sink, Mock<IContext<IMessage>> Context, LoggerSpecification<IContext<IMessage>> Sut)
        CreateHarness(IMessage message)
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var context = new Mock<IContext<IMessage>>();
        context.SetupGet(c => c.Message).Returns(message);

        return (sink, context, new LoggerSpecification<IContext<IMessage>>(logger));
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
