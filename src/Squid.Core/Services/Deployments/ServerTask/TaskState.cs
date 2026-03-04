namespace Squid.Core.Services.Deployments.ServerTask;

public static class TaskState
{
    public const string Pending = "Pending";
    public const string Executing = "Executing";
    public const string Success = "Success";
    public const string Failed = "Failed";
    public const string Cancelling = "Cancelling";
    public const string Cancelled = "Cancelled";
    public const string TimedOut = "TimedOut";

    private static readonly HashSet<string> AllStates = new(StringComparer.OrdinalIgnoreCase)
    {
        Pending, Executing, Success, Failed, Cancelling, Cancelled, TimedOut
    };

    private static readonly HashSet<string> TerminalStates = new(StringComparer.OrdinalIgnoreCase)
    {
        Success, Failed, Cancelled, TimedOut
    };

    private static readonly HashSet<string> ActiveStates = new(StringComparer.OrdinalIgnoreCase)
    {
        Executing, Cancelling
    };

    private static readonly Dictionary<string, HashSet<string>> ValidTransitions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [Pending] = new(StringComparer.OrdinalIgnoreCase) { Executing, Cancelled, TimedOut },
            [Executing] = new(StringComparer.OrdinalIgnoreCase) { Success, Failed, Cancelling },
            [Cancelling] = new(StringComparer.OrdinalIgnoreCase) { Cancelled, Failed },
            [Success] = new(StringComparer.OrdinalIgnoreCase),
            [Failed] = new(StringComparer.OrdinalIgnoreCase),
            [Cancelled] = new(StringComparer.OrdinalIgnoreCase),
            [TimedOut] = new(StringComparer.OrdinalIgnoreCase)
        };

    public static bool IsValid(string state)
        => !string.IsNullOrEmpty(state) && AllStates.Contains(state);

    public static bool IsTerminal(string state)
        => !string.IsNullOrEmpty(state) && TerminalStates.Contains(state);

    public static bool IsActive(string state)
        => !string.IsNullOrEmpty(state) && ActiveStates.Contains(state);

    public static bool IsValidTransition(string from, string to)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            return false;

        return ValidTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
    }

    public static void EnsureValidTransition(string from, string to)
    {
        if (!IsValidTransition(from, to))
            throw new InvalidStateTransitionException(from, to);
    }
}

public class InvalidStateTransitionException : InvalidOperationException
{
    public string FromState { get; }
    public string ToState { get; }

    public InvalidStateTransitionException(string from, string to)
        : base($"Invalid state transition from '{from}' to '{to}'")
    {
        FromState = from;
        ToState = to;
    }
}
