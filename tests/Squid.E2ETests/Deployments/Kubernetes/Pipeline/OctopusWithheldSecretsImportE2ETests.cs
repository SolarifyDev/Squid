using Autofac;
using Mediator.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.E2ETests.Helpers;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for secrets the Octopus API withholds (D6). A real-socket stub Octopus serves a whole space whose
/// project, library, feed, account and step carry secrets the API never returns; the production REST client, fetcher,
/// normaliser, bundle writer, planner, validator and confirmation run through the mediator against the real database.
/// Proves the API import commits instead of blocking, writes every withheld secret empty (never a username without its
/// password), lists each one by name only, records the fillable ones as <c>Missing</c>, and that a repeat import writes
/// nothing. Its own class (own database) so its imported project cannot become "already imported" for another class.
/// </summary>
[Trait("Category", "E2E")]
public class OctopusWithheldSecretsImportE2ETests : IClassFixture<OctopusWithheldSecretsImportE2ETests.ImportFixture>
{
    private const string ApiKey = "API-E2E-WITHHELD-SECRETS-KEY-1";

    private readonly ImportFixture _fixture;

    public OctopusWithheldSecretsImportE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ApiImport_WithheldSecrets_CommitsEmptyAndListed_FillableOnesMissing_RepeatWritesNothing()
    {
        await using var octopus = StubOctopusServer.Start(ApiKey).WithWithheldSecrets();

        var import = await ImportAsync(octopus);

        import.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(import));
        import.Confirm.Data.Session.Result.MissingSensitiveValues.Select(m => (m.Kind, m.Fillable)).ShouldBe(
        [
            (OctopusImportRequiredInputKind.SensitiveVariableValue, true),
            (OctopusImportRequiredInputKind.SensitiveVariableValue, true),
            (OctopusImportRequiredInputKind.FeedPassword, true),
            (OctopusImportRequiredInputKind.AccountSecret, false),
            (OctopusImportRequiredInputKind.SensitiveActionProperty, false)
        ], ignoreOrder: true);

        var stored = await StoredAsync();
        stored.SensitiveValues.ShouldAllBe(v => string.IsNullOrEmpty(v), "a withheld value is imported empty, never a placeholder");
        stored.SensitiveValues.Count.ShouldBe(2);
        stored.FeedUsername.ShouldBeNull("a username without its password would be a half-configured feed");
        stored.SecretRows.ShouldBe(["FeedSecret:Missing", "VariableSecret:Missing", "VariableSecret:Missing"], ignoreOrder: true);

        var repeat = await ImportAsync(octopus);

        repeat.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(repeat));
        repeat.PreviewPlan.Resources.ShouldNotContain(r => OctopusImportPreviewActions.WritesDestination(r.PreviewAction), customMessage: "a repeat import of an unchanged space writes nothing");
        repeat.Confirm.Data.Session.Result.MissingSensitiveValues.ShouldBeEmpty();
        (await StoredAsync()).ShouldBeEquivalentTo(stored);
    }

    private Task<OctopusImportFlowResult> ImportAsync(StubOctopusServer octopus)
        => _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromServerAsync(mediator, 1, octopus.BaseUri, ApiKey));

    private sealed record Stored(List<string> SensitiveValues, string FeedUsername, List<string> SecretRows, int SourceMapRows, int Variables);

    private Task<Stored> StoredAsync()
        => _fixture.Run<SquidDbContext, Stored>(async db => new Stored(
            await db.Set<Variable>().Where(v => v.IsSensitive).Select(v => v.Value).ToListAsync(),
            await db.Set<ExternalFeed>().Where(f => f.Name == "Private registry").Select(f => f.Username).SingleAsync(),
            await db.Set<OctopusImportSourceMap>().Where(m => OctopusImportSourceMapQueries.SecretSourceTypes.Contains(m.SourceType)).Select(m => m.SourceType + ":" + m.OutcomeState).OrderBy(r => r).ToListAsync(),
            await db.Set<OctopusImportSourceMap>().CountAsync(),
            await db.Set<Variable>().CountAsync()));

    private static string Describe(OctopusImportFlowResult import)
        => string.Join(System.Environment.NewLine, (import.Confirm.Data.Session.Result?.Resources ?? []).SelectMany(r => r.Diagnostics).Concat(import.Confirm.Data.Session.Result?.Diagnostics ?? []).Select(d => $"{d.Severity} {d.Code} [{d.SourceId}] {d.Message}"));

    /// <summary>
    /// The pipeline fixture plus what the mediator-driven import needs in a test host: the HTTP context accessor that
    /// resolving the current user consults, loopback allowed for the stub, and open authorization.
    /// </summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusWithheldSecretsImportE2ETests>
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
