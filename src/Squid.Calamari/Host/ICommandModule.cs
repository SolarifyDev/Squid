namespace Squid.Calamari.Host;

public interface ICommandModule
{
    IEnumerable<ICommandHandler> CreateHandlers();
}
