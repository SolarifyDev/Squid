using Squid.Message.Commands.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Requests.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Drives the Octopus import exactly as the API controller does: every step is a real mediator
/// message, so handler construction, Autofac resolution and the mediator middleware are all exercised.
/// </summary>
internal static class OctopusImportHandlerFlow
{
    public static async Task<UploadOctopusImportResponse> UploadAsync(IMediator mediator, int spaceId, string archivePath, string password)
    {
        await using var content = File.OpenRead(archivePath);

        var command = new UploadOctopusImportCommand { SpaceId = spaceId, Password = password, FileName = Path.GetFileName(archivePath), ContentType = "application/zip", SizeBytes = content.Length, Content = content };

        return await mediator.SendAsync<UploadOctopusImportCommand, UploadOctopusImportResponse>(command);
    }

    public static Task<ExtractOctopusImportResponse> ExtractAsync(IMediator mediator, int spaceId, Guid sessionId)
        => mediator.SendAsync<ExtractOctopusImportCommand, ExtractOctopusImportResponse>(new ExtractOctopusImportCommand { SpaceId = spaceId, SessionId = sessionId });

    public static Task<GetOctopusImportPreviewResponse> PreviewAsync(IMediator mediator, int spaceId, Guid sessionId)
        => mediator.RequestAsync<GetOctopusImportPreviewRequest, GetOctopusImportPreviewResponse>(new GetOctopusImportPreviewRequest { SpaceId = spaceId, SessionId = sessionId });

    public static Task<ValidateOctopusImportResponse> ValidateAsync(IMediator mediator, int spaceId, Guid sessionId, OctopusImportPreviewPlanDto previewPlan)
        => mediator.SendAsync<ValidateOctopusImportCommand, ValidateOctopusImportResponse>(new ValidateOctopusImportCommand { SpaceId = spaceId, SessionId = sessionId, PreviewPlan = previewPlan });

    public static Task<ConfirmOctopusImportResponse> ConfirmAsync(IMediator mediator, int spaceId, Guid sessionId)
        => mediator.SendAsync<ConfirmOctopusImportCommand, ConfirmOctopusImportResponse>(new ConfirmOctopusImportCommand { SpaceId = spaceId, SessionId = sessionId });

    public static IEnumerable<OctopusImportDiagnosticDto> AllDiagnostics(OctopusImportPreviewPlanDto plan)
        => (plan?.Diagnostics ?? []).Concat((plan?.Resources ?? []).SelectMany(r => r.Diagnostics));

    public static string DescribeBlockers(IEnumerable<OctopusImportDiagnosticDto> diagnostics)
        => string.Join("; ", diagnostics.Where(d => d.Severity == Squid.Message.Enums.OctopusImport.OctopusImportCompatibilitySeverity.Blocker).Select(d => $"{d.Code}:{d.SourceId}:{d.Message}"));
}
