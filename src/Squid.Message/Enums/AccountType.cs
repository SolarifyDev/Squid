namespace Squid.Message.Enums;

public enum AccountType
{
    None = 0,
    UsernamePassword = 1,
    SshKeyPair = 2,
    Token = 3,
    AzureSubscription = 4,
    AzureServicePrincipal = 5,
    AmazonWebServicesAccount = 6,
    AmazonWebServicesRoleAccount = 7,
    ClientCertificate = 8
}
