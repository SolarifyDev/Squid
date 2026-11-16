using Squid.Core.Services.OctopusImport.Rest;

namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>
/// The machine-readable kind of an Octopus import failure, returned as <c>SquidResponse.ErrorKind</c> so the import page
/// can choose how to recover without parsing the message. The strings are a wire contract (pinned by tests).
/// <list type="bullet">
/// <item>Resume the same session (it keeps everything already fetched): <c>Unreachable</c>, <c>Concurrency</c>, <c>Rejected</c>.</item>
/// <item>Resume with another API key: <c>Unauthorized</c> (key rejected), <c>KeyRestricted</c> (key cannot read the whole space),
/// <c>KeyUserMismatch</c> (key of another Octopus user than the one the fetch started with).</item>
/// <item>Fix the server address or space: <c>NotOctopus</c>, <c>Denied</c>, <c>NotFound</c>, <c>ServerMismatch</c>.</item>
/// <item>Not resumable, start a new session: <c>SessionNotFound</c>, <c>SessionStateConflict</c>, <c>NotServerSource</c>;
/// <c>TooLarge</c> repeats until an operator raises the named limit.</item>
/// <item>Choose projects again: <c>ProjectSelection</c> (none, too many or a malformed id); <c>ProjectSelectionBeyondRead</c> (the
/// session already reads its chosen projects; connect again to add others).</item>
/// </list>
/// The Octopus REST kinds use the <see cref="Squid.Message.Enums.OctopusImport.OctopusRestErrorKind"/> member names.
/// </summary>
public static class OctopusImportErrorKinds
{
    public const string KeyRestricted = "KeyRestricted";
    public const string KeyUserMismatch = "KeyUserMismatch";
    public const string ServerMismatch = "ServerMismatch";
    public const string NotServerSource = "NotServerSource";
    public const string SessionNotFound = "SessionNotFound";
    public const string SessionStateConflict = "SessionStateConflict";
    public const string Concurrency = "Concurrency";
    public const string ProjectSelection = "ProjectSelection";
    public const string ProjectSelectionBeyondRead = "ProjectSelectionBeyondRead";

    /// <summary>The kind of <paramref name="exception"/> itself (never of an inner exception); null when it is not an import failure.</summary>
    public static string Of(Exception exception)
        => exception switch
        {
            OctopusRestException rest => rest.Kind.ToString(),
            OctopusImportApiKeyRestrictedException => KeyRestricted,
            OctopusImportHistoryServerMismatchException { PinnedField: OctopusRestKeyBinding.UserPinnedField } => KeyUserMismatch,
            OctopusImportHistoryServerMismatchException => ServerMismatch,
            OctopusImportSessionNotServerSourceException => NotServerSource,
            OctopusImportSessionNotFoundException => SessionNotFound,
            OctopusImportSessionStateTransitionException => SessionStateConflict,
            OctopusImportSessionConcurrencyException => Concurrency,
            OctopusImportProjectSelectionInvalidException => ProjectSelection,
            OctopusImportProjectSelectionBeyondReadException => ProjectSelectionBeyondRead,
            _ => null
        };
}
