namespace Squid.Message.Enums;

public enum CommunicationStyle
{
    Unknown = 0,
    KubernetesApi = 1,
    KubernetesAgent = 2,
    OpenClaw = 3,
    Ssh = 4,
    LinuxListening = 5,
    LinuxPolling = 6,
    None = 999,
}
