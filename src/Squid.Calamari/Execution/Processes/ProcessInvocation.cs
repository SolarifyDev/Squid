namespace Squid.Calamari.Execution.Processes;

public sealed class ProcessInvocation
{
    public ProcessInvocation(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("Executable is required.", nameof(executable));

        Executable = executable;
        Arguments = arguments ?? Array.Empty<string>();
        WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory;
        EnvironmentVariables = environmentVariables;
    }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string WorkingDirectory { get; }

    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; }
}
