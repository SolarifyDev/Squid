using DbUp.Builder;
using DbUp.Engine.Output;
using DbUp.ScriptProviders;

namespace Squid.Core.Persistence;

public class DbUpRunner: IStartable
{
    private string _scriptFolderName;
    private readonly IUpgradeLog _logger;
    private readonly UpgradeEngineBuilder _builder;

    public DbUpRunner(UpgradeEngineBuilder builder, IUpgradeLog logger, string scriptFolderName)
    {
        _logger = logger;
        _builder = builder;
        _scriptFolderName = scriptFolderName;
    }

    public void Start()
    {
        _builder.WithScriptsFromFileSystem(
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty, _scriptFolderName),
                new FileSystemScriptOptions
                {
                    IncludeSubDirectories = true,
                })
            .WithTransaction()
            .WithExecutionTimeout(TimeSpan.FromMinutes(3))
            .LogTo(_logger);

        var upgradeEngine = _builder.Build();

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