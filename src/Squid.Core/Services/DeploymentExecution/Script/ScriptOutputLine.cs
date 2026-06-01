namespace Squid.Core.Services.DeploymentExecution.Script;

/// <summary>
/// A single line of script output streamed live from an agent while a script is still running.
/// Carries the raw (unmasked) text plus its source so the consumer can categorise and mask it
/// exactly as the post-completion persistence path does.
/// </summary>
public sealed record ScriptOutputLine(string Text, bool IsStdErr);

/// <summary>
/// Optional live-output sink threaded into a script execution. When set, the observer invokes it
/// with each incremental batch of new output lines as they arrive (before the script completes),
/// enabling a live log tail. When null, output is only surfaced once at completion (legacy behavior).
/// </summary>
public delegate Task ScriptOutputSink(IReadOnlyList<ScriptOutputLine> lines, CancellationToken ct);
