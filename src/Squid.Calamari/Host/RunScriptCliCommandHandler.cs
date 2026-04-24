using Squid.Calamari.Commands;

namespace Squid.Calamari.Host;

public sealed class RunScriptCliCommandHandler : ICommandHandler
{
    /// <summary>
    /// P0-B.2 (2026-04-24 audit): the tentacle transports the sensitive-variable
    /// encryption password to this process via environment variable instead of
    /// <c>--password=</c> argv. Argv is visible in <c>ps aux</c> and
    /// <c>/proc/&lt;pid&gt;/cmdline</c> (typically world-readable); env vars are
    /// visible only to the process owner / root via <c>/proc/&lt;pid&gt;/environ</c>
    /// (mode 0600). Pinned by the tentacle side as
    /// <c>LocalScriptService.CalamariSensitivePasswordEnvVar</c> — drift breaks the
    /// handshake silently (sensitive variables fail to decrypt). The identical
    /// string is pinned on both sides via tests.
    ///
    /// <para><c>--password=</c> remains supported as a fallback for tests / manual
    /// invocation, but the env var wins when both are set (the env var is the
    /// production path).</para>
    /// </summary>
    public const string SensitivePasswordEnvVar = "SQUID_CALAMARI_SENSITIVE_PASSWORD";

    public CommandDescriptor Descriptor { get; } = new(
        "run-script",
        "run-script  --script=<path> [--variables=<path>] [--sensitive=<path>] [--password=<pw>]",
        "Run a bash script with bootstrapped variables.");

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (CommandLineArguments.ContainsHelpToken(args))
        {
            UsagePrinter.PrintCommand(Descriptor, Console.Out);
            return 0;
        }

        var parsed = CommandLineArguments.ParseKeyValueArgs(args);

        if (!parsed.TryGetValue("--script", out var scriptPath) || string.IsNullOrEmpty(scriptPath))
        {
            Console.Error.WriteLine("run-script requires --script=<path>");
            return 1;
        }

        parsed.TryGetValue("--variables", out var variablesPath);
        parsed.TryGetValue("--sensitive", out var sensitivePath);
        parsed.TryGetValue("--password", out var argvPassword);

        var password = ResolvePassword(argvPassword, Environment.GetEnvironmentVariable(SensitivePasswordEnvVar));

        variablesPath ??= Path.Combine(Path.GetDirectoryName(scriptPath) ?? ".", "variables.json");

        var command = new RunScriptCommand();
        var result = await command.ExecuteWithResultAsync(scriptPath, variablesPath, sensitivePath, password, ct)
            .ConfigureAwait(false);

        return result.ExitCode;
    }

    /// <summary>
    /// P0-B.2 precedence rule: env var wins over <c>--password=</c> argv. The tentacle
    /// sets ONLY the env var post-fix; the argv fallback exists for tests and manual
    /// invocation. If env var is set to a non-empty value, it's the canonical password
    /// and argv is ignored entirely. Empty / whitespace env var falls through to argv
    /// (avoids a mistakenly-unset-but-present env var from blanking out a real argv
    /// password during tests).
    /// </summary>
    internal static string? ResolvePassword(string? argvPassword, string? envPassword)
    {
        if (!string.IsNullOrWhiteSpace(envPassword)) return envPassword;
        return argvPassword;
    }
}
