using System.IO;
using Microsoft.Extensions.Configuration;

namespace Squid.UnitTests.Setup;

/// <summary>A throw-away data directory plus helpers to build configuration pointing at it.</summary>
internal sealed class TempDataDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"squid-setup-{Guid.NewGuid():N}");

    public TempDataDirectory() => Directory.CreateDirectory(Path);

    public string ConfigFile => System.IO.Path.Combine(Path, "squid.json");

    public IConfiguration Configuration(Dictionary<string, string> values = null)
    {
        var all = new Dictionary<string, string> { ["DataDirectory"] = Path };

        foreach (var (key, value) in values ?? new()) all[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(all).Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
