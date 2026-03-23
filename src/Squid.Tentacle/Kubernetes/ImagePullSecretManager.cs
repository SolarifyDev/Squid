using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using k8s.Models;
using Serilog;
using Squid.Tentacle.Configuration;

namespace Squid.Tentacle.Kubernetes;

public class ImagePullSecretManager
{
    internal const string SecretName = "squid-registry-credentials";

    private readonly IKubernetesPodOperations _ops;
    private readonly KubernetesSettings _settings;
    private readonly ConcurrentDictionary<string, bool> _createdSecrets = new();

    public ImagePullSecretManager(IKubernetesPodOperations ops, KubernetesSettings settings)
    {
        _ops = ops;
        _settings = settings;
    }

    public bool HasCredentials => GetAllRegistries().Count > 0;

    public List<string> EnsureAllPullSecrets()
    {
        var secrets = new List<string>();

        foreach (var reg in GetAllRegistries())
        {
            var secretName = BuildSecretName(reg.Server, reg.Username);

            if (_createdSecrets.ContainsKey(secretName))
            {
                secrets.Add(secretName);
                continue;
            }

            try
            {
                CreateRegistrySecret(secretName, reg);
                _createdSecrets.TryAdd(secretName, true);
                secrets.Add(secretName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to create pull secret for {Server}", reg.Server);
            }
        }

        return secrets;
    }

    public string? EnsurePullSecret()
    {
        var secrets = EnsureAllPullSecrets();
        return secrets.Count > 0 ? secrets[0] : null;
    }

    internal static string BuildSecretName(string server, string username)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{server}:{username}"));
        var shortHash = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        return $"squid-registry-{shortHash}";
    }

    internal List<RegistryCredential> GetAllRegistries()
    {
        var registries = new List<RegistryCredential>();

        if (!string.IsNullOrEmpty(_settings.ScriptPodRegistryServer))
            registries.Add(new RegistryCredential(_settings.ScriptPodRegistryServer, _settings.ScriptPodRegistryUsername, _settings.ScriptPodRegistryPassword));

        if (!string.IsNullOrWhiteSpace(_settings.ScriptPodAdditionalRegistries))
        {
            try
            {
                var additional = JsonSerializer.Deserialize<List<RegistryCredential>>(_settings.ScriptPodAdditionalRegistries, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (additional != null)
                    registries.AddRange(additional.Where(r => !string.IsNullOrEmpty(r.Server)));
            }
            catch (JsonException ex)
            {
                Log.Warning(ex, "Failed to parse ScriptPodAdditionalRegistries JSON");
            }
        }

        return registries;
    }

    private void CreateRegistrySecret(string secretName, RegistryCredential reg)
    {
        var dockerConfigJson = BuildDockerConfigJson(reg.Server, reg.Username, reg.Password);

        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = secretName,
                NamespaceProperty = _settings.TentacleNamespace,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "kubernetes-agent",
                    ["squid.io/context-type"] = "registry-credentials"
                }
            },
            Type = "kubernetes.io/dockerconfigjson",
            Data = new Dictionary<string, byte[]>
            {
                [".dockerconfigjson"] = Encoding.UTF8.GetBytes(dockerConfigJson)
            }
        };

        HelmMetadata.ApplyHelmAnnotations(secret.Metadata, _settings);
        _ops.CreateOrReplaceSecret(secret, _settings.TentacleNamespace);

        Log.Information("Created image pull secret '{SecretName}' for registry {Server}", secretName, reg.Server);
    }

    internal static string BuildDockerConfigJson(string server, string username, string password)
    {
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

        var config = new
        {
            auths = new Dictionary<string, object>
            {
                [server] = new { username, password, auth }
            }
        };

        return JsonSerializer.Serialize(config);
    }
}

internal record RegistryCredential(string Server, string Username, string Password);
