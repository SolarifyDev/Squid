using DbUp.Engine.Output;

namespace Squid.Infrastructure.Persistence;

public class DbUpLogger<T>: IUpgradeLog where T: class
{
    private readonly ILogger _logger;

    public DbUpLogger(ILogger logger)
    {
        _logger = logger;
    }
    
    public void WriteInformation(string format, params object[] args)
    {
        _logger.Information(format,args);
    }

    public void WriteError(string format, params object[] args)
    {
        _logger.Error(format, args);
    }

    public void WriteWarning(string format, params object[] args)
    {
        _logger.Warning(format, args);
    }
}