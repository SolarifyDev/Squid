using System.Collections.Concurrent;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.ScriptExecution;

public class ScriptPodContext
{
    public ScriptPodContext(string ticketId, string podName, string workDir, string eosMarkerToken, string targetNamespace = null)
    {
        TicketId = ticketId;
        PodName = podName;
        WorkDir = workDir;
        EosMarkerToken = eosMarkerToken;
        Namespace = targetNamespace;
    }

    public string TicketId { get; }
    public string PodName { get; }
    public string WorkDir { get; }
    public string EosMarkerToken { get; }
    public string? Namespace { get; }
    public long LogSequence { get; set; }
    public long LastReadLogLength { get; set; }
    public DateTime? LastLogTimestamp { get; set; }
    public HashSet<int> RecentLineHashes { get; set; } = new();

    // EOS marker detection
    public bool EosDetected { get; set; }
    public int EosExitCode { get; set; }

    // Log rotation detection
    public bool LogTruncationDetected { get; set; }

    // Log size tracking
    public long TotalBytesRead { get; set; }
    public bool LogOutputTruncated { get; set; }

    // Sensitive output masking
    public HashSet<string> SensitiveValues { get; set; } = new(StringComparer.Ordinal);
    public byte[]? LogEncryptionKey { get; set; }

    // K8s event injection
    public ConcurrentQueue<ProcessOutput> InjectedEvents { get; } = new();

    // Log streaming
    public ConcurrentQueue<string> StreamedLogLines { get; } = new();
    public CancellationTokenSource? LogStreamCts { get; set; }
    public Task? LogStreamTask { get; set; }
}
