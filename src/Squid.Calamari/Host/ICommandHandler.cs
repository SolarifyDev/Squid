namespace Squid.Calamari.Host;

public interface ICommandHandler
{
    CommandDescriptor Descriptor { get; }

    Task<int> ExecuteAsync(string[] args, CancellationToken ct);
}
