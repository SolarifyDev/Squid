using System.IO;
using Microsoft.Extensions.Configuration;
using Squid.Core.Setup.Configuration;

namespace Squid.UnitTests.Setup;

/// <summary>
/// The data-directory file must rank above appsettings (it holds what setup decided) but below
/// environment variables (start parameters always win), and must not exist at all for deployments
/// without a data directory.
/// </summary>
[Collection(Squid.UnitTests.Support.GlobalStateSerialisedCollection.Name)]
public sealed class SquidConfigurationSourcesTests
{
    [Fact]
    public void NoDataDirectory_AddsNothing()
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["X"] = "1" });
        var before = builder.Sources.Count;

        builder.AddDataDirectoryFile();

        builder.Sources.Count.ShouldBe(before);
    }

    [Fact]
    public void FileRanksAboveBaseConfiguration_AndBelowEnvironmentVariables()
    {
        using var dir = new TempDataDirectory();
        var envKey = $"SQUIDTEST_{Guid.NewGuid():N}";

        File.WriteAllText(dir.ConfigFile, $"{{ \"Base\": \"from-file\", \"{envKey}\": \"from-file\" }}");
        Environment.SetEnvironmentVariable(envKey, "from-env");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { ["DataDirectory"] = dir.Path, ["Base"] = "from-appsettings" })
                .AddEnvironmentVariables()
                .AddDataDirectoryFile()
                .Build();

            configuration["Base"].ShouldBe("from-file");
            configuration[envKey].ShouldBe("from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }

    [Fact]
    public void EmptyEnvironmentVariable_DoesNotBlankASavedValue_ButANonEmptyOneStillWins()
    {
        using var dir = new TempDataDirectory();
        var blankKey = $"SQUIDTEST_{Guid.NewGuid():N}";
        var setKey = $"SQUIDTEST_{Guid.NewGuid():N}";

        File.WriteAllText(dir.ConfigFile, $"{{ \"{blankKey}\": \"saved\", \"{setKey}\": \"saved\" }}");
        Environment.SetEnvironmentVariable(blankKey, "");
        Environment.SetEnvironmentVariable(setKey, "from-env");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { ["DataDirectory"] = dir.Path })
                .AddEnvironmentVariables()
                .AddDataDirectoryFile()
                .Build();

            configuration[blankKey].ShouldBe("saved");
            configuration[setKey].ShouldBe("from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(blankKey, null);
            Environment.SetEnvironmentVariable(setKey, null);
        }
    }

    [Fact]
    public void MissingFile_IsOptional()
    {
        using var dir = new TempDataDirectory();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["DataDirectory"] = dir.Path }).AddDataDirectoryFile().Build();

        configuration["Anything"].ShouldBeNull();
    }
}
