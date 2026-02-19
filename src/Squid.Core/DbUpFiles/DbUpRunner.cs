using DbUp;
using DbUp.ScriptProviders;

namespace Squid.Core.DbUpFiles;

public class DbUpRunner
{
    private readonly string _connectionString;

    public DbUpRunner(string connectionString)
    {
        _connectionString = connectionString;
    }

    public void Run(string scriptFolderName, Assembly assembly)
    {
        EnsureDatabase.For.PostgresqlDatabase(_connectionString);

        var upgradeEngine = DeployChanges.To.PostgresqlDatabase(_connectionString)
            .WithScriptsFromFileSystem(
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty, scriptFolderName),
                new FileSystemScriptOptions
                {
                    IncludeSubDirectories = true
                })
            .WithScriptsAndCodeEmbeddedInAssembly(assembly, s => s.StartsWith($"{assembly.GetName().Name}.{scriptFolderName}"))
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
