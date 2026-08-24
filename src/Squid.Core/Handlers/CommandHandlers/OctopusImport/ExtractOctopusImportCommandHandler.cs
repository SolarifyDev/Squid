using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Handlers.CommandHandlers.OctopusImport;

public class ExtractOctopusImportCommandHandler(
    IOctopusImportSessionDataProvider sessionDataProvider,
    IOctopusImportSessionService sessionService,
    ICurrentUser currentUser,
    IOctopusArchiveExtractor archiveExtractor,
    IOctopusInputExtractor inputExtractor,
    IOctopusManifestInventoryBuilder inventoryBuilder,
    IOctopusResourceGraphBuilder graphBuilder)
    : ICommandHandler<ExtractOctopusImportCommand, ExtractOctopusImportResponse>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

    public async Task<ExtractOctopusImportResponse> Handle(
        IReceiveContext<ExtractOctopusImportCommand> context,
        CancellationToken cancellationToken)
    {
        var command = context.Message;
        var destinationSpaceId = GetSpaceId(command);
        var session = await GetOwnedSessionAsync(command, cancellationToken).ConfigureAwait(false);
        var state = ParseState(session.State);
        if (state != OctopusImportSessionState.Uploaded)
            throw new OctopusImportSessionStateTransitionException(state, OctopusImportSessionState.Extracted);

        if (string.IsNullOrWhiteSpace(session.TemporaryUploadPath) || !File.Exists(session.TemporaryUploadPath))
            return BadRequest(command, "Octopus import session does not have an available temporary upload.", [
                Diagnostic(
                    OctopusImportCompatibilitySeverity.Blocker,
                    "OctopusImport.Extraction.TemporaryUploadMissing",
                    "Octopus import session does not have an available temporary upload.")
            ]);

        OctopusInputExtractionResult extraction;

        try
        {
            extraction = await ExtractInputAsync(session.TemporaryUploadPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OctopusArchiveExtractionException ex)
        {
            return BadRequest(command, ex.Message, [
                Diagnostic(
                    OctopusImportCompatibilitySeverity.Blocker,
                    ex.Code,
                    ex.Message,
                    ex.SourcePath)
            ]);
        }

        var inventory = inventoryBuilder.Build(extraction);
        var graph = graphBuilder.Build(inventory);
        var result = BuildResult(extraction, inventory, graph);

        if (result.HasBlockers)
            return BadRequest(command, "Octopus import extraction produced blocking diagnostics.", result.Diagnostics, result);

        var payloadJson = JsonSerializer.Serialize(result, JsonOptions);
        var updatedSession = await sessionService
            .UpdatePayloadAndTransitionAsync(
                command.SessionId,
                destinationSpaceId,
                OctopusImportSessionState.Uploaded,
                OctopusImportSessionState.Extracted,
                redactedNormalizedDataJson: payloadJson,
                ct: cancellationToken)
            .ConfigureAwait(false);

        return new ExtractOctopusImportResponse
        {
            Code = HttpStatusCode.OK,
            Data = new ExtractOctopusImportResponseData
            {
                Session = updatedSession,
                Extraction = result
            }
        };
    }

    private async Task<OctopusImportSession> GetOwnedSessionAsync(ExtractOctopusImportCommand command, CancellationToken ct)
    {
        var destinationSpaceId = GetSpaceId(command);
        if (currentUser.Id == null)
            throw new UnauthorizedAccessException("Octopus import extraction requires an authenticated user.");

        var session = await sessionDataProvider
            .GetSessionAsync(command.SessionId, currentUser.Id.Value, destinationSpaceId, ct)
            .ConfigureAwait(false);

        if (session == null)
            throw new OctopusImportSessionNotFoundException(command.SessionId);

        return session;
    }

    private async Task<OctopusInputExtractionResult> ExtractInputAsync(string temporaryUploadPath, CancellationToken ct)
    {
        await using var stream = new FileStream(temporaryUploadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        if (await LooksLikeZipAsync(stream, ct).ConfigureAwait(false))
        {
            var archive = await archiveExtractor.ExtractZipAsync(stream, ct: ct).ConfigureAwait(false);
            return await inputExtractor.ExtractJsonEntriesAsync(archive.Entries, ct).ConfigureAwait(false);
        }

        return await inputExtractor
            .ExtractStandaloneJsonAsync(stream, Path.GetFileName(temporaryUploadPath), ct: ct)
            .ConfigureAwait(false);
    }

    private static async Task<bool> LooksLikeZipAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[ZipMagic.Length];
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length), ct).ConfigureAwait(false);
        stream.Position = 0;

        if (read < ZipMagic.Length)
            return false;

        for (var i = 0; i < ZipMagic.Length; i++)
        {
            if (header[i] != ZipMagic[i])
                return false;
        }

        return true;
    }

    private static OctopusImportExtractionResultDto BuildResult(
        OctopusInputExtractionResult extraction,
        OctopusManifestInventoryResult inventory,
        OctopusResourceGraph graph)
    {
        var diagnostics = extraction.Diagnostics
            .Concat(inventory.Diagnostics)
            .Concat(graph.Diagnostics)
            .Select(MapDiagnostic)
            .GroupBy(d => new { d.Severity, d.Code, d.Message, d.SourceId, d.ResourceType, d.ResourceName })
            .Select(g => g.First())
            .ToList();

        return OctopusImportRedaction.RedactDto(new OctopusImportExtractionResultDto
        {
            ExtractedAt = DateTimeOffset.UtcNow,
            DocumentCount = extraction.Documents.Count,
            ResourceCount = graph.Resources.Count,
            Counts = inventory.Counts
                .Select(c => new OctopusImportDocumentCountDto
                {
                    DocumentType = c.Kind.ToString(),
                    Count = c.Count
                })
                .ToList(),
            Files = extraction.Documents
                .OrderBy(d => d.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(d => new OctopusImportSourceFileSummaryDto
                {
                    Path = d.SourcePath,
                    DocumentType = d.Classification.Kind.ToString(),
                    SizeBytes = d.SizeBytes,
                    Sha256 = null
                })
                .ToList(),
            Diagnostics = diagnostics
        });
    }

    private static OctopusImportDiagnosticDto MapDiagnostic(OctopusInputExtractionDiagnostic diagnostic)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = diagnostic.Severity,
            Code = diagnostic.Code,
            Message = diagnostic.Message,
            SourceId = diagnostic.SourceId,
            ResourceType = diagnostic.DocumentKind?.ToString(),
            ResourceName = diagnostic.SourcePath
        });

    private static ExtractOctopusImportResponse BadRequest(
        ExtractOctopusImportCommand command,
        string message,
        IReadOnlyList<OctopusImportDiagnosticDto> diagnostics,
        OctopusImportExtractionResultDto extraction = null)
    {
        extraction ??= new OctopusImportExtractionResultDto
        {
            ExtractedAt = DateTimeOffset.UtcNow,
            Diagnostics = diagnostics.Select(OctopusImportRedaction.RedactDiagnostic).ToList()
        };

        return new ExtractOctopusImportResponse
        {
            Code = HttpStatusCode.BadRequest,
            Msg = message,
            Data = new ExtractOctopusImportResponseData
            {
                Session = new OctopusImportSessionDto
                {
                    SessionId = command.SessionId,
                    DestinationSpaceId = command.SpaceId ?? 0,
                    State = OctopusImportSessionState.Uploaded
                },
                Extraction = extraction
            }
        };
    }

    private static OctopusImportDiagnosticDto Diagnostic(
        OctopusImportCompatibilitySeverity severity,
        string code,
        string message,
        string sourcePath = null)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = severity,
            Code = code,
            Message = message,
            ResourceName = sourcePath
        });

    private static int GetSpaceId(ExtractOctopusImportCommand command)
        => command.SpaceId
           ?? throw new UnauthorizedAccessException("Octopus import extraction requires destination space context.");

    private static OctopusImportSessionState ParseState(string state)
        => Enum.TryParse<OctopusImportSessionState>(state, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Octopus import session state.");
}
