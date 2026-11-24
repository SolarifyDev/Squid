using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Mediator.Net.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Squid.Api.Controllers;
using Squid.Api.Filters;
using Squid.Core.Middlewares.Logging;
using Squid.Message.Commands.ProjectTriggers;
using Squid.Message.Models.ProjectTriggers;
using Squid.Message.Requests.ProjectTriggers;
using Squid.Message.Response;

namespace Squid.UnitTests.Services.ProjectTriggers.Management;

public class ProjectTriggerApiContractTests
{
    [Fact]
    public void RoutesRequireAuthenticationAndExposeAllManagementOperations()
    {
        var controller = typeof(ProjectTriggerController);
        controller.GetCustomAttribute<AuthorizeAttribute>().ShouldNotBeNull();
        controller.GetCustomAttribute<RouteAttribute>().Template.ShouldBe("api/project-triggers");
        var routes = controller.GetMethods().SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>())
            .Select(route => $"{route.HttpMethods.Single()} {route.Template}").OrderBy(x => x).ToArray();

        routes.ShouldBe(["GET detail", "GET history", "GET list", "GET types", "POST create", "POST delete", "POST disable", "POST enable", "POST update"]);
    }

    [Theory]
    [InlineData("/api/project-triggers/create", false)]
    [InlineData("/api/project-triggers/create", true)]
    [InlineData("/api/channels/create", false)]
    [InlineData("/api/channels/create", true)]
    public void UnexpectedErrorsUseTheRepositoryMessageFormat(string path, bool hasInnerException)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        var failure = new InvalidOperationException("Operation failed.", hasInnerException ? new Exception("Provider unavailable.") : null);
        var context = new ExceptionContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), [])
            { Exception = failure };

        new GlobalExceptionFilter().OnException(context);

        var response = context.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<SquidResponse>();
        response.Code.ShouldBe(HttpStatusCode.InternalServerError);
        response.Msg.ShouldBe(hasInnerException ? "Operation failed. → Provider unavailable." : "Operation failed.");
    }

    [Fact]
    public async Task MediatorLogsOmitWriteConfigurationBeforeValidation()
    {
        const string privateValue = "unrecognized-private-material";
        var config = JsonSerializer.Deserialize<JsonElement>($$"""{"schemaVersion":1,"arbitrary":"{{privateValue}}"}""");
        var sink = new Capture();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var sut = new LoggerSpecification<IContext<IMessage>>(logger);
        var context = new Mock<IContext<IMessage>>();

        foreach (var message in new IMessage[] { new CreateProjectTriggerCommand { ProjectId = 41, Configuration = config },
                     new UpdateProjectTriggerCommand { ProjectId = 41, TriggerId = 5, Configuration = config } })
        {
            context.SetupGet(c => c.Message).Returns(message);
            await sut.BeforeExecute(context.Object, default);
        }

        sink.Lines.Count.ShouldBe(2);
        sink.Lines.ShouldAllBe(line => !line.Contains(privateValue));
        sink.Lines.First().ShouldContain("41");
    }

    [Fact]
    public async Task MediatorLogsKeepDetailFieldsAndUseCommonRedaction()
    {
        const string apiKey = "opaque-test-value-129";
        const string password = "opaque-test-value-251";
        var config = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            channelId = 4,
            environmentId = 9,
            authentication = new { apiKey, password }
        });
        var sink = new Capture();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var sut = new LoggerSpecification<IContext<IMessage>>(logger);
        var context = new Mock<IContext<IMessage>>();
        context.SetupGet(c => c.Message).Returns(new GetProjectTriggerRequest { ProjectId = 41, TriggerId = 5 });
        context.SetupGet(c => c.Result).Returns(new GetProjectTriggerResponse
        {
            Data = new ProjectTriggerDto
            {
                Id = 5,
                ProjectId = 41,
                Revision = 2,
                Name = "Daily deployment",
                TriggerType = ProjectTriggerTypes.Scheduled,
                ActionType = ProjectTriggerActions.DeployNewRelease,
                Configuration = config
            }
        });

        await sut.AfterExecute(context.Object, default);

        var line = sink.Lines.Single();
        line.ShouldContain("Daily deployment");
        line.ShouldContain(ProjectTriggerTypes.Scheduled);
        line.ShouldContain(ProjectTriggerActions.DeployNewRelease);
        line.ShouldContain("channelId");
        line.ShouldContain("environmentId");
        line.ShouldContain("[redacted]");
        line.ShouldNotContain(apiKey);
        line.ShouldNotContain(password);
    }

    private sealed class Capture : ILogEventSink
    {
        public List<string> Lines { get; } = [];
        public void Emit(LogEvent message) => Lines.Add(message.RenderMessage());
    }
}
