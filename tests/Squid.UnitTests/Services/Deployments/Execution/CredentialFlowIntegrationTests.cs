using System.Linq;
using System.Text;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Certificates;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class CredentialFlowIntegrationTests
{
    private readonly Mock<IDeploymentAccountDataProvider> _accountDataProvider = new();
    private readonly Mock<ICertificateDataProvider> _certificateDataProvider = new();
    private readonly EndpointContextBuilder _builder;
    private readonly KubernetesApiEndpointVariableContributor _contributor;
    private readonly KubernetesApiContextScriptBuilder _scriptBuilder;

    public CredentialFlowIntegrationTests()
    {
        _builder = new EndpointContextBuilder(_accountDataProvider.Object, _certificateDataProvider.Object);
        _contributor = new KubernetesApiEndpointVariableContributor();
        _scriptBuilder = new KubernetesApiContextScriptBuilder();
    }

    [Theory]
    [InlineData(AccountType.Token)]
    [InlineData(AccountType.UsernamePassword)]
    [InlineData(AccountType.ClientCertificate)]
    [InlineData(AccountType.AmazonWebServicesAccount)]
    [InlineData(AccountType.AzureServicePrincipal)]
    [InlineData(AccountType.GoogleCloudAccount)]
    public async Task CredentialFlow_BuildContext_ContributeVariables_WrapScript(AccountType accountType)
    {
        var credentials = CreateCredentials(accountType);
        var credentialsJson = DeploymentAccountCredentialsConverter.Serialize(credentials);
        var account = new DeploymentAccount { Id = 1, AccountType = accountType, Credentials = credentialsJson };
        var endpointJson = MakeEndpointJson(1);

        _accountDataProvider.Setup(a => a.GetAccountByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(account);

        // Step 1: Build endpoint context
        var context = await _builder.BuildAsync(endpointJson, _contributor, CancellationToken.None);

        context.GetAccountData().ShouldNotBeNull();
        context.GetAccountData().AuthenticationAccountType.ShouldBe(accountType);

        // Step 2: Contribute variables
        var vars = _contributor.ContributeVariables(context);

        vars.ShouldContain(v => v.Name == SpecialVariables.Account.AccountType);
        var accountTypeVar = vars.First(v => v.Name == SpecialVariables.Account.AccountType);
        accountTypeVar.Value.ShouldBe(accountType.ToString());

        VerifyCredentialSpecificVariables(accountType, vars);

        // Step 3: Wrap script
        var scriptContext = new ScriptContext
        {
            Endpoint = context,
            Syntax = Message.Models.Deployments.Execution.ScriptSyntax.Bash
        };
        var wrapped = _scriptBuilder.WrapWithContext("echo test", scriptContext);

        wrapped.ShouldNotBeNull();
        wrapped.ShouldContain("echo test");
        wrapped.ShouldNotContain("{{");
    }

    [Fact]
    public async Task CredentialFlow_NoAccount_WrapScriptStillSucceeds()
    {
        var endpointJson = MakeEndpointJsonNoAccount();

        var context = await _builder.BuildAsync(endpointJson, _contributor, CancellationToken.None);

        context.GetAccountData().ShouldBeNull();

        var vars = _contributor.ContributeVariables(context);
        vars.ShouldNotBeEmpty();

        var scriptContext = new ScriptContext
        {
            Endpoint = context,
            Syntax = Message.Models.Deployments.Execution.ScriptSyntax.Bash
        };
        var wrapped = _scriptBuilder.WrapWithContext("echo test", scriptContext);

        wrapped.ShouldNotBeNull();
        wrapped.ShouldContain("echo test");
        wrapped.ShouldNotContain("{{");
    }

    [Fact]
    public async Task CredentialFlow_ClusterCertificate_IncludedInWrappedScript()
    {
        const string clusterCaPem = "-----BEGIN CERTIFICATE-----\nCLUSTERCA\n-----END CERTIFICATE-----";
        var account = new DeploymentAccount { Id = 1, AccountType = AccountType.Token, Credentials = DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = "tok" }) };
        var cert = new Certificate { Id = 5, CertificateData = Convert.ToBase64String(Encoding.UTF8.GetBytes(clusterCaPem)), CertificateDataFormat = CertificateDataFormat.Pem, HasPrivateKey = false };
        var endpointJson = MakeEndpointJsonWithClusterCert(1, 5);

        _accountDataProvider.Setup(a => a.GetAccountByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        _certificateDataProvider.Setup(c => c.GetCertificateByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(cert);

        var context = await _builder.BuildAsync(endpointJson, _contributor, CancellationToken.None);

        context.GetCertificate(EndpointResourceType.ClusterCertificate).ShouldBe(clusterCaPem);

        var scriptContext = new ScriptContext
        {
            Endpoint = context,
            Syntax = Message.Models.Deployments.Execution.ScriptSyntax.Bash
        };
        var wrapped = _scriptBuilder.WrapWithContext("echo test", scriptContext);

        var certBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(clusterCaPem));
        wrapped.ShouldContain(certBase64);
    }

    [Fact]
    public async Task CredentialFlow_ClientCertificate_EnrichedAndWrapped()
    {
        const string clientCertPem = "-----BEGIN CERTIFICATE-----\nCLIENTCERT\n-----END CERTIFICATE-----\n-----BEGIN PRIVATE KEY-----\nCLIENTKEY\n-----END PRIVATE KEY-----";
        var cert = new Certificate { Id = 5, CertificateData = Convert.ToBase64String(Encoding.UTF8.GetBytes(clientCertPem)), CertificateDataFormat = CertificateDataFormat.Pem, HasPrivateKey = true };
        var endpointJson = MakeEndpointJsonWithClientCert(5);

        _certificateDataProvider.Setup(c => c.GetCertificateByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(cert);

        var context = await _builder.BuildAsync(endpointJson, _contributor, CancellationToken.None);

        var accountData = context.GetAccountData();
        accountData.ShouldNotBeNull();
        accountData.AuthenticationAccountType.ShouldBe(AccountType.ClientCertificate);

        var scriptContext = new ScriptContext
        {
            Endpoint = context,
            Syntax = Message.Models.Deployments.Execution.ScriptSyntax.Bash
        };
        var wrapped = _scriptBuilder.WrapWithContext("echo test", scriptContext);

        var certBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(clientCertPem));
        wrapped.ShouldContain(certBase64);
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static object CreateCredentials(AccountType accountType) => accountType switch
    {
        AccountType.Token => new TokenCredentials { Token = "test-token-value" },
        AccountType.UsernamePassword => new UsernamePasswordCredentials { Username = "admin", Password = "s3cret" },
        AccountType.ClientCertificate => new ClientCertificateCredentials { ClientCertificateData = "cert-data", ClientCertificateKeyData = "key-data" },
        AccountType.AmazonWebServicesAccount => new AwsCredentials { AccessKey = "AKIATEST", SecretKey = "secret-key" },
        AccountType.AzureServicePrincipal => new AzureServicePrincipalCredentials { ClientId = "az-client", TenantId = "az-tenant", Key = "az-key" },
        AccountType.GoogleCloudAccount => new GcpCredentials { JsonKey = "gcp-json-key-data" },
        _ => throw new ArgumentOutOfRangeException(nameof(accountType))
    };

    private static void VerifyCredentialSpecificVariables(AccountType accountType, List<Message.Models.Deployments.Variable.VariableDto> vars)
    {
        switch (accountType)
        {
            case AccountType.Token:
                vars.ShouldContain(v => v.Name == SpecialVariables.Account.Token);
                break;
            case AccountType.UsernamePassword:
                vars.ShouldContain(v => v.Name == SpecialVariables.Account.Username);
                vars.ShouldContain(v => v.Name == SpecialVariables.Account.Password);
                break;
            case AccountType.ClientCertificate:
                vars.ShouldContain(v => v.Name == SpecialVariables.Account.ClientCertificateData);
                vars.ShouldContain(v => v.Name == SpecialVariables.Account.ClientCertificateKeyData);
                break;
            case AccountType.AmazonWebServicesAccount:
                vars.ShouldContain(v => v.Name == SpecialVariables.Account.AccessKey);
                vars.ShouldContain(v => v.Name == SpecialVariables.Account.SecretKey);
                break;
            case AccountType.AzureServicePrincipal:
                vars.ShouldContain(v => v.Name == SpecialVariables.Account.ClientId);
                vars.ShouldContain(v => v.Name == SpecialVariables.Account.TenantId);
                break;
            case AccountType.GoogleCloudAccount:
                vars.ShouldContain(v => v.Name == SpecialVariables.Account.GcpJsonKey);
                break;
        }
    }

    private static string MakeEndpointJson(int accountId)
    {
        return JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = "https://localhost:6443",
            SkipTlsVerification = "True",
            Namespace = "default",
            ResourceReferences = new[] { new { Type = (int)EndpointResourceType.AuthenticationAccount, ResourceId = accountId } }
        });
    }

    private static string MakeEndpointJsonNoAccount()
    {
        return JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = "https://localhost:6443",
            SkipTlsVerification = "True",
            Namespace = "default",
            ResourceReferences = Array.Empty<object>()
        });
    }

    private static string MakeEndpointJsonWithClusterCert(int accountId, int certId)
    {
        return JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = "https://localhost:6443",
            SkipTlsVerification = "True",
            Namespace = "default",
            ResourceReferences = new object[]
            {
                new { Type = (int)EndpointResourceType.AuthenticationAccount, ResourceId = accountId },
                new { Type = (int)EndpointResourceType.ClusterCertificate, ResourceId = certId }
            }
        });
    }

    private static string MakeEndpointJsonWithClientCert(int certId)
    {
        return JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = "https://localhost:6443",
            SkipTlsVerification = "True",
            Namespace = "default",
            ResourceReferences = new object[]
            {
                new { Type = (int)EndpointResourceType.ClientCertificate, ResourceId = certId }
            }
        });
    }
}
