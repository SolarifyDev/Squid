namespace Squid.Calamari.Pipeline;

public interface ITemporaryFileTrackingExecutionContext
{
    ICollection<string> TemporaryFiles { get; }
}
