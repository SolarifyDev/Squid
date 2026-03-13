using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Certificates;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed class PrepareTargetsPhase(
    ITransportRegistry transportRegistry,
    IDeploymentAccountDataProvider deploymentAccountDataProvider,
    ICertificateDataProvider certificateDataProvider) : IDeploymentPipelinePhase
{
    public int Order => 400;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        foreach (var target in ctx.AllTargets)
        {
            var tc = new DeploymentTargetContext { Machine = target };

            LoadTransportForTarget(tc);

            if (tc.Transport != null)
                await LoadAuthenticationAsync(tc, ct).ConfigureAwait(false);

            ContributeEndpointVariablesForTarget(tc);

            if (tc.Transport != null)
                await ContributeAdditionalVariablesForTargetAsync(ctx, tc, ct).ConfigureAwait(false);

            ctx.AllTargetsContext.Add(tc);
        }
    }

    private void LoadTransportForTarget(DeploymentTargetContext tc)
    {
        tc.EndpointContext = new EndpointContext { EndpointJson = tc.Machine.Endpoint };
        tc.CommunicationStyle = CommunicationStyleParser.Parse(tc.EndpointContext.EndpointJson);
        tc.Transport = transportRegistry.Resolve(tc.CommunicationStyle);
    }

    private async Task LoadAuthenticationAsync(DeploymentTargetContext tc, CancellationToken ct)
    {
        var refs = tc.Transport.Variables.ParseResourceReferences(tc.EndpointContext.EndpointJson);

        var authAccountId = refs.FindFirst(EndpointResourceType.AuthenticationAccount);

        if (authAccountId.HasValue)
            await ResolveAccountAsync(tc, authAccountId.Value, ct).ConfigureAwait(false);

        foreach (var certRef in refs.References.Where(r => r.Type is EndpointResourceType.ClientCertificate or EndpointResourceType.ClusterCertificate))
        {
            await ResolveCertificateAsync(tc, certRef, ct).ConfigureAwait(false);
        }
    }

    private async Task ResolveAccountAsync(DeploymentTargetContext tc, int accountId, CancellationToken ct)
    {
        var account = await deploymentAccountDataProvider.GetAccountByIdAsync(accountId, ct).ConfigureAwait(false);

        if (account == null) return;

        tc.EndpointContext.SetAccountData(account.AccountType, account.Credentials);
    }

    private async Task ResolveCertificateAsync(DeploymentTargetContext tc, EndpointResourceReference certRef, CancellationToken ct)
    {
        var cert = await certificateDataProvider.GetCertificateByIdAsync(certRef.ResourceId, ct).ConfigureAwait(false);

        if (cert == null) return;

        tc.EndpointContext.SetCertificate(certRef.Type, cert.CertificateData);

        if (certRef.Type == EndpointResourceType.ClientCertificate) EnrichCredentialsWithClientCertificate(tc.EndpointContext, cert);
    }

    private static void EnrichCredentialsWithClientCertificate(EndpointContext ctx, Persistence.Entities.Deployments.Certificate cert)
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

    private static void ContributeEndpointVariablesForTarget(DeploymentTargetContext tc)
    {
        if (tc.Transport == null) return;

        var endpointVars = tc.Transport.Variables.ContributeVariables(tc.EndpointContext);

        tc.EndpointVariables.AddRange(endpointVars);
    }

    private static async Task ContributeAdditionalVariablesForTargetAsync(DeploymentTaskContext ctx, DeploymentTargetContext tc, CancellationToken ct)
    {
        var additionalVars = await tc.Transport.Variables
            .ContributeAdditionalVariablesAsync(ctx.ProcessSnapshot, ctx.Release, ct).ConfigureAwait(false);

        tc.EndpointVariables.AddRange(additionalVars);
    }
}
