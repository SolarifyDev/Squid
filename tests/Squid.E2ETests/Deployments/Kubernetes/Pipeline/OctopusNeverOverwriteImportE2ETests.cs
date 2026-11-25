using Autofac;
using Mediator.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.Security;
using Squid.E2ETests.Helpers;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for the never-overwrite rule across sources. A real-socket stub Octopus serves configuration, withheld secrets
/// and history; the production REST client, fetcher, normaliser, planner, validator and confirmation import it through the mediator
/// into the real database, with the first fetch failing once and resumed on the same session. A user then edits Squid through the
/// real services (the project's variable set is saved, which re-inserts every variable) and directly (project name, release notes,
/// feed credentials). The export file of the same installation, the API again and the file again only fill what is still empty — the
/// library secret nobody set — and never write anything the user edited; a last API import is up to date. Its own class (own database).
/// </summary>
[Trait("Category", "E2E")]
public class OctopusNeverOverwriteImportE2ETests : IClassFixture<OctopusNeverOverwriteImportE2ETests.ImportFixture>
{
    private const int SpaceId = 1;
    private const string ApiKey = "API-E2E-NEVER-OVERWRITE-1";

    private readonly ImportFixture _fixture;

    public OctopusNeverOverwriteImportE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ApiResumed_UserEdits_FileApiFile_OnlyFillTheStillEmptySecret_EditsNeverOverwritten_ThenUpToDate()
    {
        await EnsureSpaceAsync();
        await using var octopus = StubOctopusServer.Start(ApiKey).WithWithheldSecrets().Release("Projects-1", "Releases-1", "1.0.0").Deployment("Projects-1", "Deployments-1", "Releases-1", "ServerTasks-1");
        var archive = OctopusSecretFillImportE2ETests.SameInstallationExport();

        (await ImportFromServerResumedAsync(octopus)).Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        await EditInSquidAsync();
        var edited = await EditedStateAsync();

        foreach (var (fromServer, step) in new[] { (false, "file"), (true, "API"), (false, "file again") })
        {
            var import = fromServer ? await ImportFromServerAsync(octopus) : await ImportFromFileAsync(archive);

            import.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: step);
            import.PreviewPlan.Resources.ShouldNotContain(r => r.PreviewAction == OctopusImportPreviewAction.Create || r.PreviewAction == OctopusImportPreviewAction.Update, customMessage: $"{step}: a repeat import writes nothing but fills");
            (await EditedStateAsync()).ShouldBe(edited, customMessage: $"{step}: nothing a user edited in Squid is ever overwritten");
            (await DecryptedAsync("Smtp.Password")).ShouldBe(OctopusWithheldSecretResources.SmtpPassword, customMessage: $"{step}: the library secret nobody set is filled from the file and kept");
        }

        var last = await ImportFromServerAsync(octopus);

        last.PreviewPlan.Summary.IsUpToDate.ShouldBeTrue();
        last.PreviewPlan.Summary.Counts.Incremental.ShouldBe(0);
        (await EditedStateAsync()).ShouldBe(edited);
    }

    // ── Imports ───────────────────────────────────────────────────────────

    // The first fetch fails at its first catalog request and is resumed on the same session, as the import page does.
    private async Task<OctopusImportFlowResult> ImportFromServerResumedAsync(StubOctopusServer octopus)
    {
        var sessionId = await _fixture.Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, ApiKey)).Data.Session.SessionId);
        octopus.FailNext("/projectgroups");

        await Should.ThrowAsync<OctopusRestException>(() => _fixture.Run<IMediator, OctopusHistoryFetchProgressDto>(mediator => OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey)));

        return await _fixture.Run<IMediator, OctopusImportFlowResult>(async mediator => await OctopusImportHandlerFlow.FinishServerImportAsync(mediator, SpaceId, sessionId, await OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey)));
    }

    private Task<OctopusImportFlowResult> ImportFromServerAsync(StubOctopusServer octopus)
        => _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromServerAsync(mediator, SpaceId, octopus.BaseUri, ApiKey));

    private Task<OctopusImportFlowResult> ImportFromFileAsync(string archive)
        => _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromFileAsync(mediator, SpaceId, archive, "e2e-export-password", confirmPairing: true));

    // ── User edits ────────────────────────────────────────────────────────

    private async Task EditInSquidAsync()
    {
        await _fixture.Run<SquidDbContext, IAtRestSecretProtector>(async (db, protector) =>
        {
            var protectedPassword = protector.Protect("user-token", 0);

            await db.Set<Project>().ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "Renamed in Squid"));
            await db.Set<Release>().ExecuteUpdateAsync(s => s.SetProperty(r => r.ReleaseNote, "notes edited in Squid"));
            await db.Set<ExternalFeed>().ExecuteUpdateAsync(s => s.SetProperty(f => f.Username, "user-robot").SetProperty(f => f.Password, protectedPassword));
        });

        await _fixture.Run<SquidDbContext, IVariableService>(async (db, variables) =>
        {
            var project = await db.Set<Project>().AsNoTracking().SingleAsync();
            var set = await db.Set<VariableSet>().AsNoTracking().SingleAsync(s => s.Id == project.VariableSetId);
            var dbPassword = await db.Set<Variable>().AsNoTracking().SingleAsync(v => v.Name == "Db.Password");
            var scopes = await db.Set<VariableScope>().AsNoTracking().Where(s => s.VariableId == dbPassword.Id).Select(s => new VariableScopeModel { ScopeType = s.ScopeType, ScopeValue = s.ScopeValue }).ToListAsync();
            var saved = new List<VariableModel> { new() { Name = "Db.Password", Value = "set-by-user", Type = dbPassword.Type, IsSensitive = true, Scopes = scopes } };

            await variables.UpdateVariableSetAsync(new UpdateVariableSetCommand { Id = set.Id, Name = set.Name, OwnerId = project.Id, OwnerType = VariableSetOwnerType.Project, SpaceId = SpaceId, Variables = saved }, CancellationToken.None);
            await db.SaveChangesAsync();
        });
    }

    // ── Squid state ──────────────────────────────────────────────────────

    private async Task<string> EditedStateAsync()
    {
        var project = await _fixture.Run<SquidDbContext, Project>(db => db.Set<Project>().AsNoTracking().SingleAsync());
        var notes = await _fixture.Run<SquidDbContext, string>(db => db.Set<Release>().AsNoTracking().Select(r => r.ReleaseNote).SingleAsync());
        var feed = await _fixture.Run<SquidDbContext, ExternalFeed>(db => db.Set<ExternalFeed>().AsNoTracking().Where(f => f.FeedType != "BuiltIn").SingleAsync()); // the space's own built-in feed is not an imported one
        var feedPassword = await _fixture.Run<IAtRestSecretProtector, string>(p => p.UnprotectAsync(feed.Password, 0));
        var counts = await _fixture.Run<SquidDbContext, string>(async db => $"{await db.Set<Project>().CountAsync()}/{await db.Set<Variable>().CountAsync()}/{await db.Set<Release>().CountAsync()}/{await db.Set<Deployment>().CountAsync()}/{await db.Set<ExternalFeed>().CountAsync(f => f.FeedType != "BuiltIn")}");

        return $"{project.Name}|{notes}|{await DecryptedAsync("Db.Password")}|{feed.Username}|{feedPassword}|{counts}";
    }

    private async Task<string> DecryptedAsync(string name)
    {
        var variable = await _fixture.Run<SquidDbContext, Variable>(db => db.Set<Variable>().AsNoTracking().SingleAsync(v => v.Name == name));

        return string.IsNullOrEmpty(variable.Value) ? variable.Value : await _fixture.Run<IVariableEncryptionService, string>(e => e.DecryptAsync(variable.Value, variable.VariableSetId));
    }

    // The space's own source-map rows (installation, export fingerprint) are live only while the space row exists.
    private Task EnsureSpaceAsync()
        => _fixture.Run<SquidDbContext>(async db =>
        {
            if (await db.Set<Space>().AnyAsync(s => s.Id == SpaceId)) return;

            db.Set<Space>().Add(new Space { Name = "Default", Slug = "default", IsDefault = true });
            await db.SaveChangesAsync();
        });

    /// <summary>
    /// The pipeline fixture plus what the mediator-driven import needs in a test host: the HTTP context accessor that resolving the
    /// current user consults, loopback allowed for the stub, and open authorization.
    /// </summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusNeverOverwriteImportE2ETests>
    {
        protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
        {
            base.RegisterOverrides(builder, configuration);

            builder.RegisterType<HttpContextAccessor>().As<IHttpContextAccessor>().SingleInstance();
            builder.RegisterInstance(new OctopusRestOptions { AllowLoopback = true });
            builder.RegisterInstance(OpenAuthorization()).As<IAuthorizationService>();
        }

        private static IAuthorizationService OpenAuthorization()
        {
            var authorization = new Moq.Mock<IAuthorizationService>();
            authorization.Setup(a => a.EnsurePermissionAsync(Moq.It.IsAny<PermissionCheckRequest>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            return authorization.Object;
        }
    }
}
