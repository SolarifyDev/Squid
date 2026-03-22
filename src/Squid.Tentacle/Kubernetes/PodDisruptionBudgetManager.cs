using k8s.Models;
using Squid.Tentacle.Configuration;
using Serilog;

namespace Squid.Tentacle.Kubernetes;

public class PodDisruptionBudgetManager
{
    private readonly IKubernetesPodOperations _podOps;
    private readonly KubernetesSettings _settings;

    public PodDisruptionBudgetManager(IKubernetesPodOperations podOps, KubernetesSettings settings)
    {
        _podOps = podOps;
        _settings = settings;
    }

    public void EnsurePdbExists()
    {
        var pdbName = BuildPdbName();

        try
        {
            var existing = _podOps.ReadPodDisruptionBudget(pdbName, _settings.TentacleNamespace);

            if (existing != null)
            {
                Log.Debug("PDB {PdbName} already exists, skipping creation", pdbName);
                return;
            }

            CreatePdb(pdbName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to ensure PDB {PdbName} exists", pdbName);
        }
    }

    public void ReconcilePdb()
    {
        EnsurePdbExists();
    }

    private void CreatePdb(string pdbName)
    {
        var labelSelector = BuildLabelSelector();

        var pdb = new V1PodDisruptionBudget
        {
            Metadata = new V1ObjectMeta
            {
                Name = pdbName,
                NamespaceProperty = _settings.TentacleNamespace,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "kubernetes-agent"
                }
            },
            Spec = new V1PodDisruptionBudgetSpec
            {
                MaxUnavailable = new IntstrIntOrString("0"),
                Selector = new V1LabelSelector
                {
                    MatchLabels = labelSelector
                }
            }
        };

        _podOps.CreatePodDisruptionBudget(pdb, _settings.TentacleNamespace);

        Log.Information("Created PDB {PdbName} for script pods", pdbName);
    }

    internal string BuildPdbName()
    {
        return string.IsNullOrEmpty(_settings.ReleaseName)
            ? "squid-script-pdb"
            : $"{_settings.ReleaseName}-squid-script-pdb";
    }

    internal Dictionary<string, string> BuildLabelSelector()
    {
        var selector = new Dictionary<string, string>
        {
            ["app.kubernetes.io/managed-by"] = "kubernetes-agent"
        };

        if (!string.IsNullOrEmpty(_settings.ReleaseName))
            selector["app.kubernetes.io/instance"] = _settings.ReleaseName;

        return selector;
    }
}
