using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public static class OctopusImportSessionStateMachine
{
    private static readonly HashSet<OctopusImportSessionState> TerminalStates =
    [
        OctopusImportSessionState.Succeeded,
        OctopusImportSessionState.Failed,
        OctopusImportSessionState.Expired
    ];

    private static readonly Dictionary<OctopusImportSessionState, HashSet<OctopusImportSessionState>> ValidTransitions = new()
    {
        [OctopusImportSessionState.Uploaded] =
        [
            OctopusImportSessionState.Extracted,
            OctopusImportSessionState.Failed,
            OctopusImportSessionState.Expired
        ],
        [OctopusImportSessionState.Extracted] =
        [
            OctopusImportSessionState.Previewed,
            OctopusImportSessionState.Failed,
            OctopusImportSessionState.Expired
        ],
        [OctopusImportSessionState.Previewed] =
        [
            OctopusImportSessionState.Validated,
            OctopusImportSessionState.Failed,
            OctopusImportSessionState.Expired
        ],
        [OctopusImportSessionState.Validated] =
        [
            OctopusImportSessionState.Importing,
            OctopusImportSessionState.Failed,
            OctopusImportSessionState.Expired
        ],
        [OctopusImportSessionState.Importing] =
        [
            OctopusImportSessionState.Succeeded,
            OctopusImportSessionState.Failed
        ],
        [OctopusImportSessionState.Succeeded] = [],
        [OctopusImportSessionState.Failed] = [],
        [OctopusImportSessionState.Expired] = []
    };

    public static bool IsTerminal(OctopusImportSessionState state) => TerminalStates.Contains(state);

    public static bool IsValidTransition(OctopusImportSessionState from, OctopusImportSessionState to)
        => ValidTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static void EnsureValidTransition(OctopusImportSessionState from, OctopusImportSessionState to)
    {
        if (!IsValidTransition(from, to))
            throw new OctopusImportSessionStateTransitionException(from, to);
    }
}
