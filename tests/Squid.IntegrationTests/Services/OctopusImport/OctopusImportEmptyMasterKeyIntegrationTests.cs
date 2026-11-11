using System.Net;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;
using Squid.Message.Enums.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Integration tier for import under the COMMITTED default configuration: an empty
/// <c>Security:VariableEncryption:MasterKey</c> with the default Strict enforcement, where
/// <see cref="VariableEncryptionService"/>'s constructor throws. The failure this guards against was an
/// Autofac resolution failure (every import handler resolves the optional export-secret store, which
/// used to build the encryption service eagerly), so it needs the REAL container and the REAL mediator
/// pipeline — a unit test with hand-built handlers cannot reproduce it.
/// </summary>
public class OctopusImportEmptyMasterKeyIntegrationTests : TestBase
{
    private const int SpaceId = 7;
    private const string ExportPassword = "fake-export-password-for-tests";

    public OctopusImportEmptyMasterKeyIntegrationTests()
        : base("OctopusImportEmptyMasterKey", "squid_it_octopus_import_empty_master_key", builder => builder.Register(c => new SecuritySetting(c.Resolve<Microsoft.Extensions.Configuration.IConfiguration>()) { MasterKey = string.Empty }).AsSelf().SingleInstance())
    {
    }

    [Fact]
    public async Task ExportWithoutSecrets_UploadsExtractsPreviewsAndValidates_WithoutEverBuildingTheEncryptionService()
    {
        var archivePath = OctopusExportSecretsArchive.Write($"NoSecrets-{Guid.NewGuid():N}");
        var restoreEnforcement = UseDefaultEnforcement();

        try
        {
            await Run<ILifetimeScope>(async scope =>
            {
                EnsureStrictEmptyMasterKey(scope);

                var mediator = scope.Resolve<IMediator>();
                var upload = await OctopusImportHandlerFlow.UploadAsync(mediator, SpaceId, archivePath, ExportPassword);

                upload.Code.ShouldBe(HttpStatusCode.OK, customMessage: upload.Msg);

                var sessionId = upload.Data.Session.SessionId;
                (await StoredPasswordAsync(scope, sessionId)).ShouldBeNull("an export without encrypted values is NotVerifiable, so its password is never retained");

                (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId)).Code.ShouldBe(HttpStatusCode.OK);

                var preview = await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId);
                preview.Code.ShouldBe(HttpStatusCode.OK, customMessage: preview.Msg);
                preview.Data.PreviewPlan.HasBlockers.ShouldBeFalse(OctopusImportHandlerFlow.DescribeBlockers(OctopusImportHandlerFlow.AllDiagnostics(preview.Data.PreviewPlan)));
                OctopusImportHandlerFlow.AllDiagnostics(preview.Data.PreviewPlan).Select(d => d.Code).ShouldNotContain(OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailable);

                var validate = await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, preview.Data.PreviewPlan);
                validate.Code.ShouldBe(HttpStatusCode.OK, customMessage: validate.Msg);
                validate.Data.Session.State.ShouldBe(OctopusImportSessionState.Validated);

                // Confirmation itself writes through ExternalFeedDataProvider / VariableDataProvider, which need the
                // master key regardless of the export (as every deployment does) — that predates the export-secret
                // store. What must hold here is that the store no longer drags the key into handlers that don't need it.
                Should.NotThrow(() => scope.Resolve<IOctopusImportExportSecretStore>());
            });
        }
        finally
        {
            restoreEnforcement();
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task ExportWithPassword_DegradesToRequiredInputsWithAnExplicitDiagnostic_InsteadOfFailingTheImport()
    {
        var archivePath = OctopusExportSecretsArchive.Write($"WithSecrets-{Guid.NewGuid():N}", sensitiveValue: "db-p@ss", exportPassword: ExportPassword);
        var restoreEnforcement = UseDefaultEnforcement();

        try
        {
            await Run<ILifetimeScope>(async scope =>
            {
                EnsureStrictEmptyMasterKey(scope);

                var mediator = scope.Resolve<IMediator>();
                var upload = await OctopusImportHandlerFlow.UploadAsync(mediator, SpaceId, archivePath, ExportPassword);

                upload.Code.ShouldBe(HttpStatusCode.OK, customMessage: upload.Msg);

                var sessionId = upload.Data.Session.SessionId;
                (await StoredPasswordAsync(scope, sessionId)).ShouldBe(OctopusImportExportSecretStore.NotRetainedMarker, "without an at-rest key the password must not be kept — not even in plaintext");

                (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId)).Code.ShouldBe(HttpStatusCode.OK);

                var preview = await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId);
                preview.Code.ShouldBe(HttpStatusCode.OK, customMessage: preview.Msg);

                var diagnostics = OctopusImportHandlerFlow.AllDiagnostics(preview.Data.PreviewPlan).ToList();
                diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailable && d.Severity == OctopusImportCompatibilitySeverity.Warning);
                diagnostics.Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => d.Code).Distinct().ShouldBe([OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing]);
                diagnostics.ShouldAllBe(d => !d.Message.Contains(ExportPassword) && !d.Message.Contains("db-p@ss"));

                scope.Resolve<IOctopusExportSecretsAccessor>().Secrets = OctopusExportSecrets.None;
                var withoutPassword = await scope.Resolve<IOctopusImportPlanningPipeline>().BuildPreviewAsync(UploadPath(scope, sessionId), SpaceId);
                RequiredInputKeys(preview.Data.PreviewPlan).ShouldBe(RequiredInputKeys(withoutPassword.PreviewPlan), customMessage: "an unretainable password must behave exactly like no password");

                var validate = await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, preview.Data.PreviewPlan);
                validate.Code.ShouldBe(HttpStatusCode.BadRequest);
                validate.Data.Validation.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailable);
                (await SessionStateAsync(scope, sessionId)).ShouldBe(OctopusImportSessionState.Previewed.ToString(), "a blocked validation must leave the session recoverable, not terminal");
            });
        }
        finally
        {
            restoreEnforcement();
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task PasswordNotRetainedForWantOfAKey_SessionStillConfirmsLikeOneWithoutPassword_OnceAKeyIsConfigured()
    {
        // The export's only secret is a feed password: without it the feed is imported for manual credential
        // setup (a Warning), so the session previews and validates. The operator then configures a master key
        // and restarts. The not-retained marker can never become readable, so confirmation must not refuse it
        // the way it refuses an envelope whose key was rotated — that would strand the session until expiry.
        var projectName = $"FeedOnly-{Guid.NewGuid():N}";
        var archivePath = OctopusExportSecretsArchive.Write(projectName, exportPassword: ExportPassword, feedPassword: "registry-token");
        var restoreEnforcement = UseDefaultEnforcement();
        var sessionId = Guid.Empty;

        try
        {
            await Run<ILifetimeScope>(async scope =>
            {
                EnsureStrictEmptyMasterKey(scope);

                var mediator = scope.Resolve<IMediator>();
                var upload = await OctopusImportHandlerFlow.UploadAsync(mediator, SpaceId, archivePath, ExportPassword);
                upload.Code.ShouldBe(HttpStatusCode.OK, customMessage: upload.Msg);

                sessionId = upload.Data.Session.SessionId;
                (await StoredPasswordAsync(scope, sessionId)).ShouldBe(OctopusImportExportSecretStore.NotRetainedMarker);
                (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId)).Code.ShouldBe(HttpStatusCode.OK);

                var preview = await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId);
                preview.Data.PreviewPlan.HasBlockers.ShouldBeFalse(OctopusImportHandlerFlow.DescribeBlockers(OctopusImportHandlerFlow.AllDiagnostics(preview.Data.PreviewPlan)));
                OctopusImportHandlerFlow.AllDiagnostics(preview.Data.PreviewPlan).ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailable && d.Message == OctopusImportPreviewDiagnosticCodes.ExportPasswordNotRetainedMessage);

                var validate = await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, preview.Data.PreviewPlan);
                validate.Code.ShouldBe(HttpStatusCode.OK, customMessage: validate.Msg);
            });

            await Run<ILifetimeScope>(async scope =>
            {
                var confirm = await OctopusImportHandlerFlow.ConfirmAsync(scope.Resolve<IMediator>(), SpaceId, sessionId);

                confirm.Code.ShouldBe(HttpStatusCode.OK, customMessage: confirm.Msg);
                confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: OctopusImportHandlerFlow.DescribeBlockers(confirm.Data.Session.Result?.Diagnostics ?? []));

                var db = scope.Resolve<SquidDbContext>();
                var feed = await db.Set<ExternalFeed>().AsNoTracking().SingleAsync(f => f.Name == OctopusExportSecretsArchive.FeedName(projectName));
                feed.PasswordHasValue.ShouldBeFalse("without a retained password the feed credentials are left for manual setup, exactly as with no password");
                (await StoredPasswordAsync(scope, sessionId)).ShouldBeNull();
            }, ConfiguredMasterKeyWithOpenAuthorization);
        }
        finally
        {
            restoreEnforcement();
            File.Delete(archivePath);
        }
    }

    private static void EnsureStrictEmptyMasterKey(ILifetimeScope scope)
    {
        // Precondition: this container really is the committed-default configuration (empty key, Strict default
        // with the enforcement env var unset by UseDefaultEnforcement).
        Should.Throw<Exception>(() => scope.Resolve<IVariableEncryptionService>(), customMessage: $"expected Strict MasterKey enforcement; is {VariableEncryptionService.EnforcementEnvVar} set?");
    }

    // Hermetic like IntegrationMasterKeyStrictDefault: unset the enforcement override so the committed DEFAULT
    // (Strict) is what the scenario runs under, whatever the ambient process exports. The service reads the env
    // var when it is constructed, so it must be set before any resolution and restored afterwards.
    private static Action UseDefaultEnforcement()
    {
        var prior = System.Environment.GetEnvironmentVariable(VariableEncryptionService.EnforcementEnvVar);
        System.Environment.SetEnvironmentVariable(VariableEncryptionService.EnforcementEnvVar, null);

        return () => System.Environment.SetEnvironmentVariable(VariableEncryptionService.EnforcementEnvVar, prior);
    }

    // The operator configured a real key and restarted: the integration appsettings' key replaces the empty one.
    // The confirm handler's permission preflight is covered by its unit tests; it is opened here to reach the
    // secrets path under test (the integration user has no team roles).
    private static void ConfiguredMasterKeyWithOpenAuthorization(ContainerBuilder builder)
    {
        builder.Register(c => new SecuritySetting(c.Resolve<Microsoft.Extensions.Configuration.IConfiguration>())).AsSelf().SingleInstance();

        var authorization = new Moq.Mock<IAuthorizationService>();
        authorization.Setup(a => a.EnsurePermissionAsync(Moq.It.IsAny<PermissionCheckRequest>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        builder.RegisterInstance(authorization.Object).As<IAuthorizationService>();
    }

    private static async Task<string> StoredPasswordAsync(ILifetimeScope scope, Guid sessionId)
        => (await scope.Resolve<SquidDbContext>().Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId)).ProtectedExportPassword;

    private static async Task<string> SessionStateAsync(ILifetimeScope scope, Guid sessionId)
        => (await scope.Resolve<SquidDbContext>().Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId)).State;

    private static string UploadPath(ILifetimeScope scope, Guid sessionId)
        => scope.Resolve<SquidDbContext>().Set<OctopusImportSession>().AsNoTracking().Single(s => s.SessionId == sessionId).TemporaryUploadPath;

    private static List<string> RequiredInputKeys(Squid.Message.Models.OctopusImport.OctopusImportPreviewPlanDto plan)
        => plan.RequiredInputs.Concat(plan.Resources.SelectMany(r => r.RequiredInputs)).Select(i => i.InputKey).Distinct().OrderBy(k => k).ToList();
}
