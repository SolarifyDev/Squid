using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;
using Squid.Core.Settings.Setup;

namespace Squid.Core.Setup.Configuration;

/// <summary>
/// Adds the data directory's <c>squid.json</c> to a configuration builder, ranked above
/// appsettings but BELOW environment variables — so anything an operator passes at start-up
/// still wins over what the setup wrote. No-op when no data directory is configured.
///
/// <para>One exception: an environment variable that is set but EMPTY (typically compose's
/// <c>${VAR}</c> with VAR unset) does not blank out a value the setup saved. Otherwise an empty
/// <c>SquidStore__ConnectionString</c> would send an installed system back into setup, and an empty
/// JWT key would replace the generated one.</para>
/// </summary>
public static class SquidConfigurationSources
{
    public static IConfigurationBuilder AddDataDirectoryFile(this IConfigurationBuilder builder)
    {
        var dataDirectory = new DataDirectorySetting(builder.Build());

        if (!dataDirectory.IsConfigured) return builder;

        var index = IndexOfFirstEnvironmentSource(builder);

        var fileSource = BuildFileSource(dataDirectory);

        builder.Sources.Insert(index, fileSource);
        RestoreValuesBlankedByEmptyEnvironment(builder, fileSource);

        return builder;
    }

    private static void RestoreValuesBlankedByEmptyEnvironment(IConfigurationBuilder builder, JsonConfigurationSource fileSource)
    {
        var effective = builder.Build();
        var saved = new ConfigurationBuilder().Add(fileSource).Build().AsEnumerable().Where(p => !string.IsNullOrEmpty(p.Value));
        var blanked = saved.Where(p => effective[p.Key] == string.Empty).ToDictionary(p => p.Key, p => p.Value);

        if (blanked.Count == 0) return;

        builder.Sources.Add(new MemoryConfigurationSource { InitialData = blanked });
    }

    private static int IndexOfFirstEnvironmentSource(IConfigurationBuilder builder)
    {
        var index = builder.Sources.ToList().FindIndex(s => s is EnvironmentVariablesConfigurationSource);

        return index < 0 ? builder.Sources.Count : index;
    }

    private static JsonConfigurationSource BuildFileSource(DataDirectorySetting dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory.Value);

        return new JsonConfigurationSource
        {
            FileProvider = new PhysicalFileProvider(Path.GetFullPath(dataDirectory.Value)),
            Path = DataDirectorySetting.ConfigurationFileName,
            Optional = true,
            ReloadOnChange = false
        };
    }
}
