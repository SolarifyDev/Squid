namespace Squid.Tentacle.ScriptExecution;

public class ScriptPodContext
{
    public ScriptPodContext(string ticketId, string podName, string workDir, string eosMarkerToken)
    {
        TicketId = ticketId;
        PodName = podName;
        WorkDir = workDir;
        EosMarkerToken = eosMarkerToken;
    }

    public string TicketId { get; }
    public string PodName { get; }
    public string WorkDir { get; }
    public string EosMarkerToken { get; }
    public long LogSequence { get; set; }
    public long LastReadLogLength { get; set; }

    // EOS marker detection
    public bool EosDetected { get; set; }
    public int EosExitCode { get; set; }
}
