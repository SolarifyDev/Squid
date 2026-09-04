using System.Net;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Handlers.CommandHandlers.OctopusImport;

public class UploadOctopusImportCommandHandler(
    IOctopusImportSessionService sessionService,
    IOctopusImportTemporaryUploadStore uploadStore)
    : ICommandHandler<UploadOctopusImportCommand, UploadOctopusImportResponse>
{
    public async Task<UploadOctopusImportResponse> Handle(
        IReceiveContext<UploadOctopusImportCommand> context,
        CancellationToken cancellationToken)
    {
        var command = context.Message;

        if (command.Content == null)
            return BadRequest("Octopus import upload requires a file stream.");
        if (string.IsNullOrWhiteSpace(command.FileName))
            return BadRequest("Octopus import upload requires a file name.");
        if (command.SizeBytes <= 0)
            return BadRequest("Octopus import upload requires a non-empty file.");

        var destinationSpaceId = GetSpaceId(command);
        var sourceSummary = BuildSourceSummary(command, null, command.SizeBytes);
        var session = await sessionService
            .CreateSessionAsync(destinationSpaceId, sourceSummary, cancellationToken)
            .ConfigureAwait(false);

        var upload = await uploadStore
            .SaveAsync(session.SessionId, command.FileName, command.Content, cancellationToken)
            .ConfigureAwait(false);

        var completedSummary = BuildSourceSummary(command, upload.Sha256, upload.SizeBytes);
        session = await sessionService
            .RegisterTemporaryUploadAsync(session.SessionId, destinationSpaceId, upload, completedSummary, cancellationToken)
            .ConfigureAwait(false);

        return new UploadOctopusImportResponse
        {
            Code = HttpStatusCode.OK,
            Data = new UploadOctopusImportResponseData
            {
                Session = session
            }
        };
    }

    private static UploadOctopusImportResponse BadRequest(string message)
        => new()
        {
            Code = HttpStatusCode.BadRequest,
            Msg = message
        };

    private static int GetSpaceId(UploadOctopusImportCommand command)
        => command.SpaceId
           ?? throw new UnauthorizedAccessException("Octopus import upload requires destination space context.");

    private static OctopusImportSourceSummaryDto BuildSourceSummary(
        UploadOctopusImportCommand command,
        string sha256,
        long sizeBytes)
    {
        return new OctopusImportSourceSummaryDto
        {
            FileName = Path.GetFileName(command.FileName),
            ContentType = command.ContentType,
            SizeBytes = sizeBytes,
            DetectedFormat = DetectFormat(command.FileName, command.ContentType),
            Sha256 = sha256
        };
    }

    private static string DetectFormat(string fileName, string contentType)
    {
        var extension = Path.GetExtension(fileName);
        if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
            return "Zip";
        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
            return "Json";
        if (contentType?.Contains("zip", StringComparison.OrdinalIgnoreCase) == true)
            return "Zip";
        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            return "Json";

        return "Unknown";
    }
}
