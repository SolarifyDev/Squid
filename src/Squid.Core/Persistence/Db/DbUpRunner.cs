using DbUp;
using DbUp.ScriptProviders;

namespace Squid.Core.Persistence.Db;

public class DbUpRunner
{
    public static readonly string ScriptFolder = Path.Combine("Persistence", "DbUpFiles");

    private readonly string _connectionString;

    public DbUpRunner(string connectionString)
    {
        _connectionString = connectionString;
    }

    public void Run()
    {
        EnsureDatabase.For.PostgresqlDatabase(_connectionString);

        var assemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        var embeddedResourcePrefix = ScriptFolder.Replace(Path.DirectorySeparatorChar, '.');

        var upgradeEngine = DeployChanges.To.PostgresqlDatabase(_connectionString)
            .WithScriptsFromFileSystem(
                Path.Combine(assemblyLocation, ScriptFolder),
                new FileSystemScriptOptions
                {
                    IncludeSubDirectories = true
                })
            .WithScriptsAndCodeEmbeddedInAssembly(
                typeof(DbUpRunner).Assembly,
                s => s.StartsWith($"{typeof(DbUpRunner).Assembly.GetName().Name}.{embeddedResourcePrefix}"))
            .WithTransaction()
            .LogToAutodetectedLog()
            .LogToConsole()
            .Build();

        var result = upgradeEngine.PerformUpgrade();

        if (!result.Successful)
        {
            if (result.ErrorScript != null)
            {
                Log.Error("DbUp failed on script: {ErrorScriptName}", result.ErrorScript.Name);
                Log.Error("DbUp failed on script content: {ErrorScriptContent}", result.ErrorScript.Contents);

                Console.WriteLine($"DbUp failed on script: {result.ErrorScript.Name}");
                Console.WriteLine(result.ErrorScript.Contents);
            }

            throw result.Error;
        }
    }
}
