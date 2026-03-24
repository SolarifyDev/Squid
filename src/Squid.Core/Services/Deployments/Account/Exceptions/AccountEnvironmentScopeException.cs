namespace Squid.Core.Services.Deployments.Account.Exceptions;

public class AccountEnvironmentScopeException : InvalidOperationException
{
    public int AccountId { get; }
    public string AccountName { get; }
    public int EnvironmentId { get; }

    public AccountEnvironmentScopeException(int accountId, string accountName, int environmentId)
        : base($"Account '{accountName}' (ID {accountId}) is not available in environment {environmentId}")
    {
        AccountId = accountId;
        AccountName = accountName;
        EnvironmentId = environmentId;
    }
}
