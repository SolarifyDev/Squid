using System.Net;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Authorization;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Integration tier (REAL Postgres, REAL container, REAL mediator pipeline) for the lifetime of the
/// retained Octopus export password: it must exist only while a session is active, every way a session
/// can end must clear it, and every failure around it must leave the session explicit and recoverable.
/// </summary>
public class OctopusImportExportPasswordLifecycleIntegrationTests : TestBase
{
    private const int SpaceId = 7;
    private const string ExportPassword = "fake-export-password-for-tests";
    private const string RetainedEnvelope = "SQUID_ENCRYPTED_V2:retained-password-envelope";

    /// <summary>
    /// Every public member of the session persistence/service seams, classified. A member that can move a
    /// session into a terminal state is exercised by <see cref="EverySessionEndingPath_ClearsTheRetainedExportPassword"/>;
    /// adding a member without classifying it fails <see cref="EverySessionSeamMember_IsClassified"/>.
    /// </summary>
    private static readonly Dictionary<string, bool> EndsSession = new()
    {
        [$"{nameof(IOctopusImportSessionDataProvider)}.{nameof(IOctopusImportSessionDataProvider.AddSessionAsync)}"] = false,
        [$"{nameof(IOctopusImportSessionDataProvider)}.{nameof(IOctopusImportSessionDataProvider.GetSessionAsync)}"] = false,
        [$"{nameof(IOctopusImportSessionDataProvider)}.{nameof(IOctopusImportSessionDataProvider.GetSessionNoTrackingAsync)}"] = false,
        [$"{nameof(IOctopusImportSessionDataProvider)}.{nameof(IOctopusImportSessionDataProvider.UpdateSessionAsync)}"] = true,
        [$"{nameof(IOctopusImportSessionDataProvider)}.{nameof(IOctopusImportSessionDataProvider.TransitionStateAsync)}"] = true,
        [$"{nameof(IOctopusImportSessionDataProvider)}.{nameof(IOctopusImportSessionDataProvider.TryStartConfirmationAsync)}"] = false,
        [$"{nameof(IOctopusImportSessionDataProvider)}.{nameof(IOctopusImportSessionDataProvider.ExpireSessionsAsync)}"] = true,
        [$"{nameof(IOctopusImportSessionDataProvider)}.{nameof(IOctopusImportSessionDataProvider.MarkInterruptedImportsFailedAsync)}"] = true,
        [$"{nameof(IOctopusImportSessionDataProvider)}.{nameof(IOctopusImportSessionDataProvider.GetTemporaryUploadCleanupCandidatesAsync)}"] = false,
        [$"{nameof(IOctopusImportSessionDataProvider)}.{nameof(IOctopusImportSessionDataProvider.MarkTemporaryUploadCleanedAsync)}"] = true,
        [$"{nameof(IOctopusImportSessionDataProvider)}.{nameof(IOctopusImportSessionDataProvider.MarkTemporaryUploadCleanupFailedAsync)}"] = false,
        [$"{nameof(IOctopusImportSessionService)}.{nameof(IOctopusImportSessionService.CreateSessionAsync)}"] = false,
        [$"{nameof(IOctopusImportSessionService)}.{nameof(IOctopusImportSessionService.GetSessionAsync)}"] = false,
        [$"{nameof(IOctopusImportSessionService)}.{nameof(IOctopusImportSessionService.RegisterTemporaryUploadAsync)}"] = false,
        [$"{nameof(IOctopusImportSessionService)}.{nameof(IOctopusImportSessionService.UpdatePayloadAndTransitionAsync)}"] = true,
        [$"{nameof(IOctopusImportSessionService)}.{nameof(IOctopusImportSessionService.TryStartConfirmationAsync)}"] = false,
        [$"{nameof(IOctopusImportSessionService)}.{nameof(IOctopusImportSessionService.RecordResultAsync)}"] = true,
        [$"{nameof(IOctopusImportSessionService)}.{nameof(IOctopusImportSessionService.ExpireSessionsAsync)}"] = true
    };

    public OctopusImportExportPasswordLifecycleIntegrationTests()
        : base("OctopusImportExportPasswordLifecycle", "squid_it_octopus_import_export_password_lifecycle")
    {
    }

    // ── Guard: every session-ending path clears the password ───────────────────────────────────

    [Fact]
    public void EverySessionSeamMember_IsClassified()
    {
        var members = new[] { typeof(IOctopusImportSessionDataProvider), typeof(IOctopusImportSessionService) }
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(method => $"{type.Name}.{method.Name}"))
            .Distinct()
            .ToList();

        members.Where(member => !EndsSession.ContainsKey(member)).ShouldBeEmpty("classify the new member in EndsSession; if it can end a session it must clear ProtectedExportPassword and be added to SessionEndingPaths");
        EndsSession.Keys.Where(member => !members.Contains(member)).ShouldBeEmpty("a classified member no longer exists — remove it");
    }

    public static TheoryData<string, OctopusImportSessionState, OctopusImportSessionState> SessionEndingPaths()
    {
        var data = new TheoryData<string, OctopusImportSessionState, OctopusImportSessionState>();
        var states = Enum.GetValues<OctopusImportSessionState>();

        foreach (var from in states)
        {
            foreach (var to in states.Where(to => OctopusImportSessionStateMachine.IsTerminal(to) && OctopusImportSessionStateMachine.IsValidTransition(from, to)))
            {
                data.Add(nameof(IOctopusImportSessionService.UpdatePayloadAndTransitionAsync), from, to);
                data.Add(nameof(IOctopusImportSessionService.RecordResultAsync), from, to);
                data.Add(nameof(IOctopusImportSessionDataProvider.TransitionStateAsync), from, to);
                data.Add(nameof(IOctopusImportSessionDataProvider.UpdateSessionAsync), from, to);
            }

            if (!OctopusImportSessionStateMachine.IsTerminal(from) && from != OctopusImportSessionState.Importing)
                data.Add(nameof(IOctopusImportSessionDataProvider.ExpireSessionsAsync), from, OctopusImportSessionState.Expired);
        }

        data.Add(nameof(IOctopusImportSessionDataProvider.MarkInterruptedImportsFailedAsync), OctopusImportSessionState.Importing, OctopusImportSessionState.Failed);

        // The upload purge only runs for terminal sessions, but must not trust that they were cleared.
        foreach (var terminal in states.Where(OctopusImportSessionStateMachine.IsTerminal))
            data.Add(nameof(IOctopusImportSessionDataProvider.MarkTemporaryUploadCleanedAsync), terminal, terminal);

        return data;
    }

    [Theory]
    [MemberData(nameof(SessionEndingPaths))]
    public async Task EverySessionEndingPath_ClearsTheRetainedExportPassword(string path, OctopusImportSessionState from, OctopusImportSessionState to)
    {
        var sessionId = Guid.NewGuid();
        var uploadPath = $"/tmp/{sessionId:N}.zip";

        await Run<ILifetimeScope>(async scope =>
        {
            var db = scope.Resolve<SquidDbContext>();
            var session = Session(sessionId, from, RetainedEnvelope, uploadPath);
            if (path == nameof(IOctopusImportSessionDataProvider.ExpireSessionsAsync)) session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            if (path == nameof(IOctopusImportSessionDataProvider.MarkInterruptedImportsFailedAsync)) session.LastStateChangedAt = DateTimeOffset.UtcNow.AddHours(-2);
            db.Set<OctopusImportSession>().Add(session);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await EndSessionAsync(scope, path, sessionId, from, to, uploadPath);

            var stored = await db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId);
            stored.State.ShouldBe(to.ToString(), customMessage: $"{path} did not move the session {from} -> {to}");
            stored.ProtectedExportPassword.ShouldBeNull($"{path} ended the session ({from} -> {to}) but kept the export password");
            (await TerminalSessionsHoldingAPasswordAsync(db)).ShouldBe(0);
        });
    }

    [Fact]
    public async Task NonTerminalTransitions_KeepTheRetainedExportPassword()
    {
        var sessionId = Guid.NewGuid();

        await Run<ILifetimeScope>(async scope =>
        {
            var db = scope.Resolve<SquidDbContext>();
            db.Set<OctopusImportSession>().Add(Session(sessionId, OctopusImportSessionState.Extracted, RetainedEnvelope, "/tmp/keep.zip"));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var sessions = scope.Resolve<IOctopusImportSessionService>();
            await sessions.UpdatePayloadAndTransitionAsync(sessionId, SpaceId, OctopusImportSessionState.Extracted, OctopusImportSessionState.Previewed);
            await sessions.UpdatePayloadAndTransitionAsync(sessionId, SpaceId, OctopusImportSessionState.Previewed, OctopusImportSessionState.Validated, validatedPlanJson: "{}");
            (await sessions.TryStartConfirmationAsync(sessionId, SpaceId)).ShouldBeTrue();

            (await db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId)).ProtectedExportPassword.ShouldBe(RetainedEnvelope, "confirmation still needs it while the session is active");
        });
    }

    // ── Upload: retention, failure compensation ──────────────────────────────────────────────

    [Fact]
    public async Task Upload_ExportWithoutSecrets_IsNotVerifiable_AndNeverRetainsThePassword()
    {
        var archivePath = OctopusExportSecretsArchive.Write($"NotVerifiable-{Guid.NewGuid():N}");

        try
        {
            await Run<ILifetimeScope>(async scope =>
            {
                var upload = await OctopusImportHandlerFlow.UploadAsync(scope.Resolve<IMediator>(), SpaceId, archivePath, ExportPassword);

                upload.Code.ShouldBe(HttpStatusCode.OK, customMessage: upload.Msg);
                (await StoredSessionAsync(scope, upload.Data.Session.SessionId)).ProtectedExportPassword.ShouldBeNull();
            });
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task Upload_RecordingThePasswordFails_LeavesAFailedSessionWithoutPassword_NotAnImportableOrphan()
    {
        var archivePath = OctopusExportSecretsArchive.Write($"StoreFails-{Guid.NewGuid():N}", sensitiveValue: "db-p@ss", exportPassword: ExportPassword);
        var sessionsBefore = 0;

        try
        {
            await Run<ILifetimeScope>(async scope =>
            {
                var db = scope.Resolve<SquidDbContext>();
                sessionsBefore = await db.Set<OctopusImportSession>().CountAsync();

                await Should.ThrowAsync<InvalidOperationException>(() => OctopusImportHandlerFlow.UploadAsync(scope.Resolve<IMediator>(), SpaceId, archivePath, ExportPassword));

                var created = await db.Set<OctopusImportSession>().AsNoTracking().OrderByDescending(s => s.Id).FirstAsync();
                (await db.Set<OctopusImportSession>().CountAsync()).ShouldBe(sessionsBefore + 1);
                created.State.ShouldBe(OctopusImportSessionState.Failed.ToString(), "a session whose proven password could not be recorded must not stay importable");
                created.ProtectedExportPassword.ShouldBeNull();
                created.CompletedAt.ShouldNotBeNull();
                created.TemporaryUploadCleanupAfter.ShouldNotBeNull("the archive must be scheduled for purge");
                created.ResultJson.ShouldContain(OctopusImportPreviewDiagnosticCodes.UploadAbandoned);
            }, builder => builder.RegisterType<FailingSecretStore>().As<IOctopusImportExportSecretStore>().InstancePerLifetimeScope());
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    // ── Master key changed between Validate and Confirm ──────────────────────────────────────

    [Fact]
    public async Task Confirm_MasterKeyChangedAfterValidation_RefusesAndKeepsTheSessionRecoverable_ThenSucceedsOnceTheKeyIsBack()
    {
        var projectName = $"KeyRotation-{Guid.NewGuid():N}";
        var archivePath = OctopusExportSecretsArchive.Write(projectName, sensitiveValue: "db-p@ss", exportPassword: ExportPassword);
        var sessionId = Guid.Empty;

        try
        {
            await Run<ILifetimeScope>(async scope =>
            {
                var mediator = scope.Resolve<IMediator>();
                sessionId = (await OctopusImportHandlerFlow.UploadAsync(mediator, SpaceId, archivePath, ExportPassword)).Data.Session.SessionId;
                (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId)).Code.ShouldBe(HttpStatusCode.OK);

                var preview = await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId);
                preview.Data.PreviewPlan.HasBlockers.ShouldBeFalse(OctopusImportHandlerFlow.DescribeBlockers(OctopusImportHandlerFlow.AllDiagnostics(preview.Data.PreviewPlan)));

                var validate = await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, preview.Data.PreviewPlan);
                validate.Code.ShouldBe(HttpStatusCode.OK, customMessage: validate.Msg);
            });

            await Run<ILifetimeScope>(async scope =>
            {
                var confirm = await OctopusImportHandlerFlow.ConfirmAsync(scope.Resolve<IMediator>(), SpaceId, sessionId);

                confirm.Code.ShouldBe(HttpStatusCode.BadRequest);
                confirm.Data.BlockerSummary.BlockingReasons.Select(r => r.Code).ShouldBe([OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailable]);

                var stored = await StoredSessionAsync(scope, sessionId);
                stored.State.ShouldBe(OctopusImportSessionState.Validated.ToString(), "an unreadable password must not end the session");
                stored.ProtectedExportPassword.ShouldNotBeNull("the envelope is kept so the original key can still read it");
                (await scope.Resolve<SquidDbContext>().Set<Project>().AnyAsync(p => p.Name == projectName)).ShouldBeFalse();
            }, builder => builder.Register(_ => RotatedKeyEncryption()).As<IVariableEncryptionService>().InstancePerLifetimeScope());

            await Run<ILifetimeScope>(async scope =>
            {
                var confirm = await OctopusImportHandlerFlow.ConfirmAsync(scope.Resolve<IMediator>(), SpaceId, sessionId);

                confirm.Code.ShouldBe(HttpStatusCode.OK, customMessage: confirm.Msg);
                confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: OctopusImportHandlerFlow.DescribeBlockers(confirm.Data.Session.Result?.Diagnostics ?? []));

                var db = scope.Resolve<SquidDbContext>();
                var project = await db.Set<Project>().SingleAsync(p => p.Name == projectName);
                var secret = await db.Set<Variable>().SingleAsync(v => v.VariableSetId == project.VariableSetId && v.Name == "Db:Password");
                (await scope.Resolve<IVariableEncryptionService>().DecryptAsync(secret.Value, project.VariableSetId)).ShouldBe("db-p@ss");
                (await StoredSessionAsync(scope, sessionId)).ProtectedExportPassword.ShouldBeNull();
            }, builder => builder.RegisterInstance(AllowAllAuthorization()).As<IAuthorizationService>());
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static async Task EndSessionAsync(ILifetimeScope scope, string path, Guid sessionId, OctopusImportSessionState from, OctopusImportSessionState to, string uploadPath)
    {
        var sessions = scope.Resolve<IOctopusImportSessionService>();
        var provider = scope.Resolve<IOctopusImportSessionDataProvider>();
        var ownerId = CurrentUsers.InternalUser.Id;
        var now = DateTimeOffset.UtcNow;

        switch (path)
        {
            case nameof(IOctopusImportSessionService.UpdatePayloadAndTransitionAsync):
                await sessions.UpdatePayloadAndTransitionAsync(sessionId, SpaceId, from, to);
                break;
            case nameof(IOctopusImportSessionService.RecordResultAsync):
                await sessions.RecordResultAsync(sessionId, SpaceId, to, new OctopusImportSessionResultDto());
                break;
            case nameof(IOctopusImportSessionDataProvider.TransitionStateAsync):
                (await provider.TransitionStateAsync(sessionId, ownerId, SpaceId, from, to)).ShouldBe(1);
                break;
            case nameof(IOctopusImportSessionDataProvider.UpdateSessionAsync):
                var tracked = await provider.GetSessionAsync(sessionId, ownerId, SpaceId);
                tracked.State = to.ToString();
                await provider.UpdateSessionAsync(tracked, tracked.DataVersion);
                break;
            case nameof(IOctopusImportSessionDataProvider.ExpireSessionsAsync):
                (await sessions.ExpireSessionsAsync(now)).ShouldBeGreaterThanOrEqualTo(1);
                break;
            case nameof(IOctopusImportSessionDataProvider.MarkInterruptedImportsFailedAsync):
                (await provider.MarkInterruptedImportsFailedAsync(now, now.AddHours(-1), TimeSpan.FromHours(6))).ShouldBeGreaterThanOrEqualTo(1);
                break;
            case nameof(IOctopusImportSessionDataProvider.MarkTemporaryUploadCleanedAsync):
                (await provider.MarkTemporaryUploadCleanedAsync(sessionId, uploadPath, now)).ShouldBe(1);
                break;
            default:
                throw new InvalidOperationException($"Unknown session-ending path {path}");
        }
    }

    private static Task<int> TerminalSessionsHoldingAPasswordAsync(SquidDbContext db)
    {
        var terminal = Enum.GetValues<OctopusImportSessionState>().Where(OctopusImportSessionStateMachine.IsTerminal).Select(s => s.ToString()).ToList();

        return db.Set<OctopusImportSession>().CountAsync(s => terminal.Contains(s.State) && s.ProtectedExportPassword != null);
    }

    private static Task<OctopusImportSession> StoredSessionAsync(ILifetimeScope scope, Guid sessionId)
        => scope.Resolve<SquidDbContext>().Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId);

    private static OctopusImportSession Session(Guid sessionId, OctopusImportSessionState state, string protectedPassword, string uploadPath)
        => new()
        {
            SessionId = sessionId,
            DestinationSpaceId = SpaceId,
            OwnerUserId = CurrentUsers.InternalUser.Id,
            State = state.ToString(),
            SourceSummaryJson = "{}",
            DataVersion = Guid.NewGuid().ToByteArray(),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            LastStateChangedAt = DateTimeOffset.UtcNow,
            ProtectedExportPassword = protectedPassword,
            TemporaryUploadPath = uploadPath
        };

    // A different, valid 32-byte master key: what the server sees after the key was rotated/replaced.
    private static VariableEncryptionService RotatedKeyEncryption()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Security:VariableEncryption:MasterKey"] = Convert.ToBase64String(key) }).Build();

        return new VariableEncryptionService(new SecuritySetting(configuration));
    }

    // The confirm handler's per-resource permission preflight is covered by its unit tests; the integration
    // user has no team roles, so it is opened here to reach the secrets path under test.
    private static IAuthorizationService AllowAllAuthorization()
    {
        var authorization = new Moq.Mock<IAuthorizationService>();
        authorization.Setup(a => a.EnsurePermissionAsync(Moq.It.IsAny<PermissionCheckRequest>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        return authorization.Object;
    }

    private sealed class FailingSecretStore : IOctopusImportExportSecretStore
    {
        public Task StoreAsync(Guid sessionId, int destinationSpaceId, string password, CancellationToken ct = default) => throw new InvalidOperationException("simulated failure while recording the export password");

        public Task<OctopusExportSecrets> LoadAsync(Guid sessionId, int destinationSpaceId, CancellationToken ct = default) => Task.FromResult(OctopusExportSecrets.None);
    }
}
