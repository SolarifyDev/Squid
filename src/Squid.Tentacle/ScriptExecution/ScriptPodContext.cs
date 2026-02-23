namespace Squid.Tentacle.ScriptExecution;

public class ScriptPodContext
{
    public ScriptPodContext(string ticketId, string podName, string workDir)
    {
        TicketId = ticketId;
        PodName = podName;
        WorkDir = workDir;
    }

    public string TicketId { get; }
    public string PodName { get; }
    public string WorkDir { get; }
    public long LogSequence { get; set; }
    public long LastReadLogLength { get; set; }
}
