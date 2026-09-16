using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Mediator.Net;
using Mediator.Net.Contracts;
using Squid.Api.Controllers;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Controllers;

public class OctopusImportControllerTests
{
    [Fact]
    public void UploadLimitConvention_AppliesConfiguredRequestAndMultipartLimitsOnlyToUpload()
    {
        var convention = new OctopusImportUploadLimitConvention(
            new OctopusArchiveExtractionOptions { MaxUploadSizeBytes = 1234 });
        var controllerModel = new ControllerModel(typeof(OctopusImportController).GetTypeInfo(), []);
        var uploadAction = new ActionModel(
            typeof(OctopusImportController).GetMethod(nameof(OctopusImportController.UploadAsync)),
            [])
        {
            Controller = controllerModel
        };
        var statusAction = new ActionModel(
            typeof(OctopusImportController).GetMethod(nameof(OctopusImportController.StatusAsync)),
            [])
        {
            Controller = controllerModel
        };

        convention.Apply(uploadAction);
        convention.Apply(statusAction);

        ((IRequestSizeLimitMetadata)uploadAction.Filters.OfType<RequestSizeLimitAttribute>().Single()).MaxRequestBodySize.ShouldBe(1234);
        uploadAction.Filters.OfType<RequestFormLimitsAttribute>().Single().MultipartBodyLengthLimit.ShouldBe(1234);
        statusAction.Filters.ShouldBeEmpty();
    }

    [Fact]
    public async Task UploadAsync_MapsPasswordIntoCommand()
    {
        var mediator = new Mock<IMediator>();
        UploadOctopusImportCommand captured = null;

        mediator
            .Setup(m => m.SendAsync<UploadOctopusImportCommand, UploadOctopusImportResponse>(It.IsAny<UploadOctopusImportCommand>(), It.IsAny<CancellationToken>()))
            .Callback<UploadOctopusImportCommand, CancellationToken>((command, _) => captured = command)
            .ReturnsAsync(new UploadOctopusImportResponse
            {
                Code = System.Net.HttpStatusCode.OK,
                Data = new UploadOctopusImportResponseData
                {
                    Session = new OctopusImportSessionDto
                    {
                        SessionId = Guid.NewGuid(),
                        DestinationSpaceId = 7,
                        State = OctopusImportSessionState.Uploaded
                    }
                }
            });

        var controller = new OctopusImportController(mediator.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("zip"));
        var file = new FormFile(stream, 0, stream.Length, "file", "export.zip")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/zip"
        };
        var request = new OctopusImportController.UploadOctopusImportForm
        {
            File = file,
            Password = "octopus-password",
            SpaceId = 7
        };

        var result = await controller.UploadAsync(request, CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        captured.ShouldNotBeNull();
        captured.Password.ShouldBe("octopus-password");
        captured.ContentType.ShouldBe("application/zip");
    }
}
