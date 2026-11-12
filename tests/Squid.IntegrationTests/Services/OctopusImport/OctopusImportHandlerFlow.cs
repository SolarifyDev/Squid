using Squid.Message.Commands.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Requests.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Drives the Octopus import exactly as the API controller does: every step is a real mediator
/// message, so handler construction, Autofac resolution and the mediator middleware are all exercised.
/// </summary>
public static class OctopusImportHandlerFlow
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

    public static Task<ConfirmOctopusImportResponse> ConfirmAsync(IMediator mediator, int spaceId, Guid sessionId, bool runInBackground = false)
        => mediator.SendAsync<ConfirmOctopusImportCommand, ConfirmOctopusImportResponse>(new ConfirmOctopusImportCommand { SpaceId = spaceId, SessionId = sessionId, RunInBackground = runInBackground });

    public static Task<ConnectOctopusImportResponse> ConnectAsync(IMediator mediator, int spaceId)
        => mediator.SendAsync<ConnectOctopusImportCommand, ConnectOctopusImportResponse>(new ConnectOctopusImportCommand { SpaceId = spaceId });

    public static Task<FetchOctopusImportHistoryResponse> FetchHistoryAsync(IMediator mediator, int spaceId, Guid sessionId, Uri serverUri, string apiKey)
        => mediator.SendAsync<FetchOctopusImportHistoryCommand, FetchOctopusImportHistoryResponse>(new FetchOctopusImportHistoryCommand { SpaceId = spaceId, SessionId = sessionId, ServerUrl = serverUri.ToString(), ApiKey = apiKey, OctopusSpaceId = "Spaces-1" });

    /// <summary>Fetches chunk after chunk, as the UI does, until the server history is complete.</summary>
    public static async Task<OctopusHistoryFetchProgressDto> FetchHistoryUntilDoneAsync(IMediator mediator, int spaceId, Guid sessionId, Uri serverUri, string apiKey, int maxChunks = 50)
    {
        for (var chunk = 0; chunk < maxChunks; chunk++)
        {
            var response = await FetchHistoryAsync(mediator, spaceId, sessionId, serverUri, apiKey);

            if (response.Data.Done) return response.Data;
        }

        throw new InvalidOperationException($"History fetch did not finish within {maxChunks} chunks.");
    }

    /// <summary>
    /// The whole server-source import as the UI drives it: a session (an uploaded export when given, else API-only),
    /// the history fetch until done, extract, preview, validate (posting the plan without its history rows, which the
    /// server owns) and a synchronous confirm. Any step that is refused throws with its reason.
    /// </summary>
    public static async Task<OctopusImportFlowResult> ImportFromServerAsync(IMediator mediator, int spaceId, Uri serverUri, string apiKey, string exportPath = null, string exportPassword = null)
    {
        var sessionId = exportPath == null ? (await ConnectAsync(mediator, spaceId)).Data.Session.SessionId : await UploadSessionAsync(mediator, spaceId, exportPath, exportPassword);
        var fetch = await FetchHistoryUntilDoneAsync(mediator, spaceId, sessionId, serverUri, apiKey);

        return await ExtractPreviewValidateConfirmAsync(mediator, spaceId, sessionId, fetch, WithoutHistoryRows);
    }

    /// <summary>An export-file-only import: upload, extract, preview, validate and a synchronous confirm.</summary>
    public static async Task<OctopusImportFlowResult> ImportFromFileAsync(IMediator mediator, int spaceId, string exportPath, string exportPassword)
    {
        var sessionId = await UploadSessionAsync(mediator, spaceId, exportPath, exportPassword);

        return await ExtractPreviewValidateConfirmAsync(mediator, spaceId, sessionId, null, plan => plan);
    }

    private static async Task<Guid> UploadSessionAsync(IMediator mediator, int spaceId, string exportPath, string exportPassword)
    {
        var upload = await UploadAsync(mediator, spaceId, exportPath, exportPassword);

        if (upload.Code != System.Net.HttpStatusCode.OK) throw new InvalidOperationException($"Upload refused: {upload.Msg}");

        return upload.Data.Session.SessionId;
    }

    private static async Task<OctopusImportFlowResult> ExtractPreviewValidateConfirmAsync(IMediator mediator, int spaceId, Guid sessionId, OctopusHistoryFetchProgressDto fetch, Func<OctopusImportPreviewPlanDto, OctopusImportPreviewPlanDto> postedPlan)
    {
        var extract = await ExtractAsync(mediator, spaceId, sessionId);

        if (extract.Code != System.Net.HttpStatusCode.OK) throw new InvalidOperationException($"Extract refused: {DescribeBlockers(extract.Data.Extraction.Diagnostics)}");

        var preview = (await PreviewAsync(mediator, spaceId, sessionId)).Data.PreviewPlan;

        if (preview.HasBlockers) throw new InvalidOperationException($"Preview blocked: {DescribeBlockers(AllDiagnostics(preview))}");

        var validate = await ValidateAsync(mediator, spaceId, sessionId, postedPlan(preview));

        if (validate.Code != System.Net.HttpStatusCode.OK) throw new InvalidOperationException($"Validate refused: {validate.Msg} {DescribeBlockers(validate.Data?.Validation?.Diagnostics ?? [])}");

        return new OctopusImportFlowResult(sessionId, fetch, preview, await ConfirmAsync(mediator, spaceId, sessionId));
    }

    // The UI omits history rows from the validate body; the server replaces them with its own.
    private static OctopusImportPreviewPlanDto WithoutHistoryRows(OctopusImportPreviewPlanDto plan)
        => new() { GeneratedAt = plan.GeneratedAt, Diagnostics = plan.Diagnostics, RequiredInputs = plan.RequiredInputs, Resources = plan.Resources.Where(r => r.SourceType is not ("Release" or "Deployment" or "ServerTask")).ToList() };

    public static IEnumerable<OctopusImportDiagnosticDto> AllDiagnostics(OctopusImportPreviewPlanDto plan)
        => (plan?.Diagnostics ?? []).Concat((plan?.Resources ?? []).SelectMany(r => r.Diagnostics));

    public static string DescribeBlockers(IEnumerable<OctopusImportDiagnosticDto> diagnostics)
        => string.Join("; ", diagnostics.Where(d => d.Severity == Squid.Message.Enums.OctopusImport.OctopusImportCompatibilitySeverity.Blocker).Select(d => $"{d.Code}:{d.SourceId}:{d.Message}"));
}

/// <summary>What one full import run saw and produced: the fetch progress (server sources only), the preview plan and the confirm response.</summary>
public sealed record OctopusImportFlowResult(Guid SessionId, OctopusHistoryFetchProgressDto Fetch, OctopusImportPreviewPlanDto PreviewPlan, ConfirmOctopusImportResponse Confirm);
