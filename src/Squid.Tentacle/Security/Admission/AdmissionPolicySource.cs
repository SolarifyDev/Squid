using Serilog;

namespace Squid.Tentacle.Security.Admission;

/// <summary>
/// Loads a policy from a file on disk. Missing file → empty policy (allow-all);
/// malformed file → keep last-good policy (fail-closed would lock out the
/// agent on a broken YAML push). Hot-reload: a FileSystemWatcher re-parses on
/// change so operators can tighten rules without restarting the agent. K8s
/// ConfigMap mounts symlink-swap the target file; watcher re-subscribes on
/// FileNotFound ticks so the swap is survived.
/// </summary>
public interface IAdmissionPolicySource
{
    AdmissionPolicy Current { get; }

    event Action<AdmissionPolicy>? Updated;
}

public sealed class FileAdmissionPolicySource : IAdmissionPolicySource, IDisposable
{
    private readonly string _filePath;
    private readonly Func<string, AdmissionPolicy> _parser;
    private readonly FileSystemWatcher? _watcher;
    private AdmissionPolicy _current = AdmissionPolicy.Empty();
    private int _disposed;

    public FileAdmissionPolicySource(string filePath, Func<string, AdmissionPolicy> parser)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));

        ReloadSafe();

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            _watcher = new FileSystemWatcher(directory)
            {
                Filter = Path.GetFileName(_filePath),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (_, _) => ReloadSafe();
            _watcher.Created += (_, _) => ReloadSafe();
            _watcher.Renamed += (_, _) => ReloadSafe();
        }
    }

    public AdmissionPolicy Current => Volatile.Read(ref _current);

    public event Action<AdmissionPolicy>? Updated;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _watcher?.Dispose();
    }

    private void ReloadSafe()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                Volatile.Write(ref _current, AdmissionPolicy.Empty());
                Updated?.Invoke(_current);
                return;
            }

            var content = File.ReadAllText(_filePath);
            var policy = _parser(content);
            Volatile.Write(ref _current, policy);
            Updated?.Invoke(policy);
            Log.Information("[Admission] Policy reloaded from {Path} with {Count} rule(s)", _filePath, policy.Rules.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Admission] Failed to parse policy at {Path} — keeping previous policy", _filePath);
        }
    }
}
