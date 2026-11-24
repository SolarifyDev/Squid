namespace Squid.Message.Models.Setup;

public sealed record DatabaseConnectionTestRequest
{
    public SetupServiceMode Mode { get; init; }
    public string ConnectionString { get; init; } = string.Empty;
}

public sealed record DatabaseConnectionTestResult
{
    public bool Success { get; init; }
    public string ServerVersion { get; init; }
    public bool DatabaseExists { get; init; }
    public bool CanCreateDatabase { get; init; }
    public string Message { get; init; } = string.Empty;
}
