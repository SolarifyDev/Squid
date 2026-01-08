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
        var coreAssembly = Assembly.GetExecutingAssembly(); // Squid.Core.dll
        
        // 选项3：明确指定
        var scriptAssembly = Assembly.GetAssembly(typeof(DbUpRunner));

        var rr = Path.GetDirectoryName(coreAssembly.Location);
        var rr2 = Path.GetDirectoryName(scriptAssembly.Location);
        
        var path = Path.Combine(Path.GetDirectoryName(Assembly.GetAssembly(typeof(DbUpRunner))?.Location) ?? string.Empty, _scriptFolderName);
        var path2 = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty, _scriptFolderName);
        
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