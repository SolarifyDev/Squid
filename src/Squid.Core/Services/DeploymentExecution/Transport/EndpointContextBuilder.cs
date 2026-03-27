using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Certificates;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public interface IEndpointContextBuilder : IScopedDependency
{
    Task<EndpointContext> BuildAsync(string endpointJson, IEndpointVariableContributor variables, CancellationToken ct);
}

public class EndpointContextBuilder(
    IDeploymentAccountDataProvider accountDataProvider,
    ICertificateDataProvider certificateDataProvider) : IEndpointContextBuilder
{
    public async Task<EndpointContext> BuildAsync(string endpointJson, IEndpointVariableContributor variables, CancellationToken ct)
    {
        var context = new EndpointContext { EndpointJson = endpointJson };

        var refs = variables.ParseResourceReferences(endpointJson);

        await ResolveAccountAsync(context, refs, ct).ConfigureAwait(false);
        await ResolveCertificatesAsync(context, refs, ct).ConfigureAwait(false);

        return context;
    }

    private async Task ResolveAccountAsync(EndpointContext context, Variables.EndpointResourceReferences refs, CancellationToken ct)
    {
        var accountId = refs.FindFirst(EndpointResourceType.AuthenticationAccount);
        if (!accountId.HasValue) return;

        var account = await accountDataProvider.GetAccountByIdAsync(accountId.Value, ct).ConfigureAwait(false);
        if (account == null) return;

        context.SetAccountData(account.AccountType, account.Credentials);
    }

    private async Task ResolveCertificatesAsync(EndpointContext context, Variables.EndpointResourceReferences refs, CancellationToken ct)
    {
        foreach (var certRef in refs.References.Where(r => r.Type is EndpointResourceType.ClientCertificate or EndpointResourceType.ClusterCertificate))
        {
            var cert = await certificateDataProvider.GetCertificateByIdAsync(certRef.ResourceId, ct).ConfigureAwait(false);
            if (cert == null) continue;

            context.SetCertificate(certRef.Type, cert.CertificateData);

            if (certRef.Type == EndpointResourceType.ClientCertificate)
                EnrichCredentialsWithClientCertificate(context, cert);
        }
    }

    internal static void EnrichCredentialsWithClientCertificate(EndpointContext ctx, Certificate cert)
    {
        var accountData = ctx.GetAccountData() ?? new ResolvedAuthenticationAccountData { AuthenticationAccountType = AccountType.ClientCertificate };
        var creds = DeserializeOrCreateCredentials(accountData);

        creds.ClientCertificateData = cert.CertificateData;

        if (cert.HasPrivateKey)
            creds.ClientCertificateKeyData = cert.CertificateData;

        ctx.SetAccountData(accountData.AuthenticationAccountType, DeploymentAccountCredentialsConverter.Serialize(creds));
    }

    private static ClientCertificateCredentials DeserializeOrCreateCredentials(ResolvedAuthenticationAccountData accountData)
    {
        if (accountData.AuthenticationAccountType == AccountType.ClientCertificate && !string.IsNullOrEmpty(accountData.CredentialsJson))
        {
            var existing = DeploymentAccountCredentialsConverter.Deserialize(AccountType.ClientCertificate, accountData.CredentialsJson);

            if (existing is ClientCertificateCredentials cc) return cc;
        }

        return new ClientCertificateCredentials();
    }
}
