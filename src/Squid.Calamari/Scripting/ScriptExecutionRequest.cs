using Squid.Calamari.Execution;

namespace Squid.Calamari.Scripting;

public sealed class ScriptExecutionRequest
{
    public required string ScriptPath { get; init; }

    public required string WorkingDirectory { get; init; }

    public ScriptSyntax Syntax { get; init; } = ScriptSyntax.Bash;

    public required ScriptOutputProcessor OutputProcessor { get; init; }

    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
}
