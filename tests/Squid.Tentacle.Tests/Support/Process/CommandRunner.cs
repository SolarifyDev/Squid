using System.Diagnostics;

namespace Squid.Tentacle.Tests.Support.Process;

public sealed class CommandResult
{
    public required int ExitCode { get; init; }
    public required string StdOut { get; init; }
    public required string StdErr { get; init; }
}

public static class CommandRunner
{
    public static async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken ct)
        => await RunAsync(fileName, arguments, workingDirectory, environment: null, ct).ConfigureAwait(false);

    public static async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken ct)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        if (environment != null)
        {
            foreach (var (key, value) in environment)
                process.StartInfo.Environment[key] = value;
        }

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            StdOut = await stdoutTask,
            StdErr = await stderrTask
        };
    }
}
