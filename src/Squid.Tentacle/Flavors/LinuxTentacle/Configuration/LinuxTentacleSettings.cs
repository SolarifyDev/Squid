namespace Squid.Tentacle.Flavors.LinuxTentacle.Configuration;

public class LinuxTentacleSettings
{
    public string WorkspacePath { get; set; } = "/opt/squid/work";
    public int ListeningPort { get; set; } = 10933;
}
