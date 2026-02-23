using Squid.Calamari.Compatibility.Octopus;

namespace Squid.Calamari.Host;

public static class CalamariCommandRegistryFactory
{
    public static CommandRegistry CreateDefault()
    {
        return new CommandRegistry(
        [
            new RunScriptCliCommandHandler(),
            new ApplyYamlCliCommandHandler(),
            new KubernetesApplyRawYamlCompatCommandHandler()
        ]);
    }
}
