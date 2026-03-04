using System.Diagnostics;
using System.Reflection;

namespace Squid.Calamari.Tests.TestSupport;

public static class CalamariTestHost
{
    public static async Task<InvocationResult> InvokeInProcessAsync(params string[] args)
    {
        var assembly = typeof(Squid.Calamari.Commands.RunScriptCommand).Assembly;
        var entryPoint = assembly.EntryPoint
                         ?? throw new InvalidOperationException("Squid.Calamari entry point not found.");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalErr = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var returnValue = entryPoint.Invoke(null, [args]);
            var exitCode = await UnwrapExitCodeAsync(returnValue).ConfigureAwait(false);

            return new InvocationResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    public static async Task<InvocationResult> InvokeCliAsync(
        string? workingDirectory = null,
        params string[] args)
    {
        var calamariAssemblyPath = typeof(Squid.Calamari.Commands.RunScriptCommand).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add(calamariAssemblyPath);
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().ConfigureAwait(false);

        return new InvocationResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }

    private static async Task<int> UnwrapExitCodeAsync(object? returnValue)
    {
        return returnValue switch
        {
            int i => i,
            Task<int> task => await task.ConfigureAwait(false),
            Task task => await task.ContinueWith(_ => 0).ConfigureAwait(false),
            null => 0,
            _ => throw new InvalidOperationException(
                $"Unsupported entry point return type: {returnValue.GetType().FullName}")
        };
    }

    public sealed record InvocationResult(int ExitCode, string Stdout, string Stderr);
}
