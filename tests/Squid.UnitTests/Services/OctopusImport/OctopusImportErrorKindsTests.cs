using System.Linq;
using FluentValidation;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportErrorKindsTests
{
    // The kinds are a wire contract the import page branches on (resume, reconnect, choose a space, start again):
    // every literal is pinned here, so a rename is a visible decision, never an invisible refactor.
    public static IEnumerable<object[]> ImportFailures()
    {
        yield return [new OctopusRestException(OctopusRestErrorKind.Unauthorized), "Unauthorized"];
        yield return [new OctopusRestException(OctopusRestErrorKind.NotOctopus), "NotOctopus"];
        yield return [new OctopusRestException(OctopusRestErrorKind.Unreachable), "Unreachable"];
        yield return [new OctopusRestException(OctopusRestErrorKind.TooLarge, "SQUID_OCTOPUS_IMPORT_HISTORY_MAX_BYTES"), "TooLarge"];
        yield return [new OctopusRestException(OctopusRestErrorKind.Denied), "Denied"];
        yield return [new OctopusRestException(OctopusRestErrorKind.NotFound), "NotFound"];
        yield return [new OctopusRestException(OctopusRestErrorKind.Rejected), "Rejected"];
        yield return [new OctopusRestException(OctopusRestErrorKind.Tls), "Tls"];
        yield return [new OctopusImportApiKeyRestrictedException(new OctopusServerPermissionsDto { OctopusSpaceId = "Spaces-1", Restricted = ["VariableView"] }), "KeyRestricted"];
        yield return [new OctopusImportHistoryServerMismatchException(OctopusRestKeyBinding.UserPinnedField), "KeyUserMismatch"];
        yield return [new OctopusImportHistoryServerMismatchException("server"), "ServerMismatch"];
        yield return [new OctopusImportHistoryServerMismatchException("space"), "ServerMismatch"];
        yield return [new OctopusImportSessionNotServerSourceException(Guid.NewGuid()), "NotServerSource"];
        yield return [new OctopusImportSessionNotFoundException(Guid.NewGuid()), "SessionNotFound"];
        yield return [new OctopusImportSessionStateTransitionException(OctopusImportSessionState.Expired, OctopusImportSessionState.Uploaded), "SessionStateConflict"];
        yield return [new OctopusImportSessionConcurrencyException(Guid.NewGuid()), "Concurrency"];
        yield return [new OctopusImportProjectSelectionInvalidException(OctopusImportProjectSelectionProblem.Empty, "none"), "ProjectSelection"];
        yield return [new OctopusImportProjectSelectionBeyondReadException(Guid.NewGuid()), "ProjectSelectionBeyondRead"];
        yield return [new OctopusImportHistoryStagingLostException(), "StagingLost"];
        yield return [new OctopusImportHistoryStagingIncompleteException("Release", 4999, 5000), "StagingLost"];
        yield return [new OctopusImportStorageException(), "Storage"];
        yield return [new OctopusImportSessionExpiredException(Guid.Empty, default, TimeSpan.FromHours(168)), "SessionExpired"];
    }

    [Theory]
    [MemberData(nameof(ImportFailures))]
    public void Of_ImportFailure_NamesItsKind(Exception exception, string expectedKind)
    {
        OctopusImportErrorKinds.Of(exception).ShouldBe(expectedKind);
    }

    [Fact]
    public void Of_EveryOctopusRestErrorKind_IsMapped()
    {
        var mapped = ImportFailures().Select(f => f[0]).OfType<OctopusRestException>().Select(e => e.Kind);

        mapped.ShouldBe(Enum.GetValues<OctopusRestErrorKind>(), ignoreOrder: true, customMessage: "a new OctopusRestErrorKind needs a pinned wire string here");
    }

    public static IEnumerable<object[]> OtherFailures()
    {
        yield return [new InvalidOperationException("boom")];
        yield return [new ValidationException("bad input")];
        yield return [new UnauthorizedAccessException("signed out")];
        yield return [new PermissionDeniedException(Permission.VariableEdit, "no edit")];
        yield return [new Exception("outer", new OctopusRestException(OctopusRestErrorKind.Unreachable))];
    }

    // Only the failure itself is classified: an import failure wrapped by something else is not this endpoint's import error.
    [Theory]
    [MemberData(nameof(OtherFailures))]
    public void Of_OtherFailure_IsNull(Exception exception)
    {
        OctopusImportErrorKinds.Of(exception).ShouldBeNull();
    }

    // Staging lives in the database: losing it means the session was cleaned up (or an earlier Squid started it), never "point every
    // replica at a shared volume". The incomplete variant names the kind and the counts only.
    [Fact]
    public void StagingLost_SaysStartANewSession_WithoutSharedVolumeAdvice()
    {
        var lost = new OctopusImportHistoryStagingLostException().Message;
        var incomplete = new OctopusImportHistoryStagingIncompleteException("Release", 4999, 5000).Message;

        lost.ShouldContain("start a new import session");
        lost.ShouldNotContain("RootPath");
        lost.ShouldNotContain("replica");
        incomplete.ShouldEndWith("Release: 4999 stored, 5000 read.");
    }
}
