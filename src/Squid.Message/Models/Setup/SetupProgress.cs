using System.Text.Json.Serialization;

namespace Squid.Message.Models.Setup;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupInstallState
{
    Idle,
    Running,
    Completed,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupStepStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped
}

/// <summary>Answer to <c>GET /api/setup/progress</c>.</summary>
public sealed record SetupProgress
{
    public SetupInstallState State { get; init; }
    public IReadOnlyList<SetupStepProgress> Steps { get; init; } = Array.Empty<SetupStepProgress>();
    public string Error { get; init; }
}

public sealed record SetupStepProgress
{
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public SetupStepStatus Status { get; init; }
    public string Message { get; init; }
}
