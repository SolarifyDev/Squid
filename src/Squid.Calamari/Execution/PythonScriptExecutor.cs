using Squid.Calamari.Execution.Output;
using Squid.Calamari.Execution.Processes;

namespace Squid.Calamari.Execution;

/// <summary>
/// PR-10 — executes a Python script file, streaming stdout/stderr through
/// <see cref="ScriptOutputProcessor"/>. Sibling of
/// <see cref="BashScriptExecutor"/> + <see cref="PowerShellScriptExecutor"/>.
///
/// <para><b>Interpreter resolution</b>: prefers <c>python3</c> (the
/// unambiguous Python-3 launcher on Linux / macOS), falls back to
/// <c>python</c> (Windows installs + some minimal containers expose only
/// <c>python</c>). Returns a clear "Python not installed" error when
/// neither is on PATH — no silent fallthrough to bash.</para>
///
/// <para><b>Arguments</b>: <c>-u</c> (unbuffered stdout/stderr so the
/// captured-log layer streams output in real time instead of after the
/// process exits) + the script path.</para>
/// </summary>
public class PythonScriptExecutor
{
    private readonly IProcessRunner _processRunner;
    private readonly Func<string?> _pythonResolver;

    public PythonScriptExecutor()
        : this(new ProcessRunner(), ResolvePythonBinary)
    {
    }

    public PythonScriptExecutor(IProcessRunner processRunner)
        : this(processRunner, ResolvePythonBinary)
    {
    }

    internal PythonScriptExecutor(IProcessRunner processRunner, Func<string?> pythonResolver)
    {
        _processRunner = processRunner;
        _pythonResolver = pythonResolver;
    }

    public async Task<int> ExecuteAsync(
        string scriptPath,
        string workDir,
        ScriptOutputProcessor outputProcessor,
        CancellationToken ct)
    {
        var binary = _pythonResolver()
                     ?? throw new InvalidOperationException(
                         "Python executor: neither 'python3' nor 'python' was found on PATH. " +
                         "Install Python 3 (https://www.python.org/downloads/) on this agent OR run the " +
                         "script as bash by renaming it to .sh.");

        var invocation = new ProcessInvocation(
            executable: binary,
            arguments: ["-u", scriptPath],
            workingDirectory: workDir);

        var result = await _processRunner.ExecuteAsync(invocation, outputProcessor.OutputSink, ct)
            .ConfigureAwait(false);

        return result.ExitCode;
    }

    /// <summary>
    /// Find python3 / python on PATH. python3 first (unambiguous Python-3),
    /// then python (Windows / minimal-container fallback). Returns the
    /// absolute path of the first found, or null.
    /// </summary>
    public static string? ResolvePythonBinary()
    {
        var py3 = FindOnPath(OperatingSystem.IsWindows() ? "python3.exe" : "python3");
        if (py3 is not null) return py3;

        var py = FindOnPath(OperatingSystem.IsWindows() ? "python.exe" : "python");
        return py;
    }

    private static string? FindOnPath(string executableName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        var separator = OperatingSystem.IsWindows() ? ';' : ':';

        foreach (var dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, executableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Invalid path chars in a PATH segment — skip silently.
            }
        }

        return null;
    }
}
