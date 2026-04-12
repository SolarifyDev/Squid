using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Runtime;

/// <summary>
/// Input for <see cref="IRuntimeBundle.Wrap"/>. Carries the user script body plus the
/// information a bundle needs to emit variable exports and squid helper metadata
/// (work directory, base directory, server task id).
/// </summary>
public sealed record RuntimeBundleWrapContext
{
    /// <summary>Raw, variable-substituted user script body. May be null or empty.</summary>
    public string UserScriptBody { get; init; }

    /// <summary>Remote per-task working directory (e.g. <c>$HOME/.squid/Work/42</c>).</summary>
    public required string WorkDirectory { get; init; }

    /// <summary>Remote base directory hosting <c>Work/</c> and <c>Packages/</c> (e.g. <c>$HOME/.squid</c>).</summary>
    public required string BaseDirectory { get; init; }

    /// <summary>Server task id for the current deployment.</summary>
    public required int ServerTaskId { get; init; }

    /// <summary>
    /// Variables to expose inside the user script. Non-sensitive entries are exported as
    /// environment variables (after name sanitization); sensitive entries are skipped so
    /// they cannot leak via <c>env</c> or process listings.
    /// </summary>
    public IReadOnlyList<VariableDto> Variables { get; init; } = Array.Empty<VariableDto>();
}
