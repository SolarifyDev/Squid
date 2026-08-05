namespace Squid.Calamari.Execution.Output;

public sealed class ConsoleProcessOutputSink : IProcessOutputSink
{
    public void WriteStdout(string line)
    {
        // Service messages are forwarded, NOT suppressed. This process's stdout is not a
        // user-facing surface — it is the wire back to the server: the Tentacle captures it
        // into the script's log lines, and those lines are the server's ONLY source of output
        // variables (ExecuteStepsPhase.CaptureOutputVariables -> ParseOutputVariables). Dropping
        // the line here silently lost every output variable a Calamari-run script emitted, while
        // the direct (non-Calamari) path passed the same line straight through. The dedicated
        // service-message sink still collects the variable for this process's own conventions;
        // forwarding is what lets the SERVER see it too.
        Console.WriteLine(line);
    }

    public void WriteStderr(string line)
    {
        Console.Error.WriteLine(line);
    }
}
