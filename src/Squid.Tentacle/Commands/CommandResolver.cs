namespace Squid.Tentacle.Commands;

/// <summary>
/// Routes the raw CLI arguments to the correct <see cref="ITentacleCommand"/>.
/// Extracted from Program.cs so the routing logic is unit-testable.
/// </summary>
public static class CommandResolver
{
    /// <summary>
    /// Pick a command for the given args. The returned <c>RemainingArgs</c> is what the
    /// selected command should receive (verb stripped for subcommand form).
    /// <c>HelpRequested</c> is true when the user asked for help — caller prints and exits.
    /// <c>UnknownCommand</c> is non-null when the first arg looks like a verb but matches
    /// no command — caller prints an error + help and exits non-zero.
    /// </summary>
    public static CommandRoute Resolve(IReadOnlyList<ITentacleCommand> commands, string[] args)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
            return CommandRoute.ForCommand(commands.OfType<RunCommand>().FirstOrDefault() ?? commands[0], args);

        var first = args[0];

        // Help flags trump everything else — must come before the `-`/`--` → run default.
        if (IsHelpFlag(first))
            return CommandRoute.ForHelp();

        // Leading `-` or `--` is a configuration flag, not a verb → default to run.
        if (first.StartsWith('-'))
            return CommandRoute.ForCommand(commands.OfType<RunCommand>().FirstOrDefault() ?? commands[0], args);

        var verb = first.ToLowerInvariant();
        var matched = commands.FirstOrDefault(c => c.Name.Equals(verb, StringComparison.OrdinalIgnoreCase));

        if (matched != null)
            return CommandRoute.ForCommand(matched, args[1..]);

        return CommandRoute.ForUnknown(verb);
    }

    public static bool IsHelpFlag(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return false;

        return arg.Equals("help", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("--help", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("-h", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("-?", StringComparison.Ordinal)
            || arg.Equals("/?", StringComparison.Ordinal);
    }
}

public sealed record CommandRoute
{
    public ITentacleCommand Command { get; init; }
    public string[] RemainingArgs { get; init; }
    public bool HelpRequested { get; init; }
    public string UnknownCommand { get; init; }

    public static CommandRoute ForCommand(ITentacleCommand command, string[] remainingArgs)
        => new() { Command = command, RemainingArgs = remainingArgs };

    public static CommandRoute ForHelp()
        => new() { HelpRequested = true };

    public static CommandRoute ForUnknown(string verb)
        => new() { UnknownCommand = verb };
}
