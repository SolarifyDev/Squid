using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mediator.Net;
using Mediator.Net.Contracts;
using Squid.Api.Controllers;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Controllers;

public class OctopusImportControllerTests
{
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
