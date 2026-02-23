namespace Squid.Calamari.Pipeline;

public interface IPathBasedExecutionContext
{
    string InputPath { get; }

    string? WorkingDirectory { get; set; }
}
