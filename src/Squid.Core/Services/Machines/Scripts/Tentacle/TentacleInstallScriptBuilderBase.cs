using Squid.Message.Commands.Machine;

namespace Squid.Core.Services.Machines.Scripts.Tentacle;

/// <summary>
/// Base for install-script builders. Subclasses only need to implement <see cref="BuildContent"/>
/// plus expose the identifying metadata properties.
/// </summary>
public abstract class TentacleInstallScriptBuilderBase : ITentacleInstallScriptBuilder
{
    public abstract string Id { get; }
    public abstract string Label { get; }
    public abstract string OperatingSystem { get; }
    public abstract string InstallationMethod { get; }
    public abstract string ScriptType { get; }
    public virtual bool IsRecommended => false;

    public TentacleInstallScript Build(TentacleInstallContext context)
    {
        return new TentacleInstallScript
        {
            Id = Id,
            Label = Label,
            OperatingSystem = OperatingSystem,
            InstallationMethod = InstallationMethod,
            ScriptType = ScriptType,
            IsRecommended = IsRecommended,
            Content = BuildContent(context)
        };
    }

    protected abstract string BuildContent(TentacleInstallContext context);

    protected static string JoinLines(params string[] lines) => string.Join(" \\\n", lines);
}
