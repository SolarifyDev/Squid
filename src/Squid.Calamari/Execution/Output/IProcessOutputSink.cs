namespace Squid.Calamari.Execution.Output;

public interface IProcessOutputSink
{
    void WriteStdout(string line);

    void WriteStderr(string line);
}
