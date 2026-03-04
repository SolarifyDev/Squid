namespace Squid.Tentacle.Tests.Support.Environment;

public sealed record KubernetesIntegrationPrerequisites(
    bool HasHelm,
    bool HasKubectl,
    bool HasKind)
{
    public bool IsAvailable => HasHelm && HasKubectl && HasKind;

    public string DescribeMissing()
    {
        var missing = new List<string>();
        if (!HasHelm) missing.Add("helm");
        if (!HasKubectl) missing.Add("kubectl");
        if (!HasKind) missing.Add("kind");
        return missing.Count == 0 ? string.Empty : string.Join(", ", missing);
    }

    public static KubernetesIntegrationPrerequisites Detect()
        => new(
            HasHelm: ExternalToolProbe.HasHelm(),
            HasKubectl: ExternalToolProbe.HasKubectl(),
            HasKind: ExternalToolProbe.HasKind());
}
