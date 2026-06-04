namespace Squid.Tentacle.Flavors.Tentacle.Configuration;

public class TentacleFlavorSettings
{
    public string WorkspacePath { get; set; } = "/opt/squid/work";
    public int ListeningPort { get; set; } = 10933;
}
