using DbUp;
using DbUp.Engine.Output;
using DbUp.ScriptProviders;

namespace Squid.Core.Persistence.Postgres;

public class PostgresDbUp: IStartable
{
    private readonly string _connectionString;
    private readonly IUpgradeLog _logger;

    public PostgresDbUp(string connectionString, IUpgradeLog logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public void Start()
    {
        EnsureDatabase.For.PostgresqlDatabase(_connectionString);
        
        var engineBuilder = DeployChanges.To
            .PostgresqlDatabase(_connectionString)
            .WithScriptsFromFileSystem(
                Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ??
                    throw new ArgumentNullException(), "Persistence/Postgres/Scripts"),
                new FileSystemScriptOptions
                {
                    IncludeSubDirectories = true,
                })
            .WithTransaction()
            .WithExecutionTimeout(TimeSpan.FromMinutes(3))
            .LogTo(_logger);

        var upgradeEngine = engineBuilder.Build();

        if (upgradeEngine.IsUpgradeRequired())
        {
            _logger.WriteInformation("Upgrades have been detected. Upgrading database now...");

            var upgradeResult = upgradeEngine.PerformUpgrade();

            if (upgradeResult.Successful)
            {
                _logger.WriteInformation("Upgrade completed successfully.");
            }
            else
            {
                _logger.WriteError(upgradeResult.Error.Message);
            }
        }
        else
        {
            _logger.WriteInformation("There is no script to be executed.");
        }
    }
}