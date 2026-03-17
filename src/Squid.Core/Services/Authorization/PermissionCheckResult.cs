namespace Squid.Core.Services.Authorization;

public class PermissionCheckResult
{
    public bool IsAuthorized { get; set; }
    public string Reason { get; set; }

    public static PermissionCheckResult Authorized() => new() { IsAuthorized = true };

    public static PermissionCheckResult Denied(string reason) => new() { IsAuthorized = false, Reason = reason };
}
