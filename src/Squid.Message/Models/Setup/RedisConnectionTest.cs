namespace Squid.Message.Models.Setup;

public sealed record RedisConnectionTestRequest
{
    public string ConnectionString { get; init; } = string.Empty;
}

public sealed record RedisConnectionTestResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}
