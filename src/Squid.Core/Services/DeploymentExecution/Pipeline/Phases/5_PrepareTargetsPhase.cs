using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Account.Exceptions;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed class PrepareTargetsPhase(
    ITransportRegistry transportRegistry,
    IEndpointContextBuilder endpointContextBuilder,
    IDeploymentAccountDataProvider deploymentAccountDataProvider) : IDeploymentPipelinePhase
{
    public int Order => 400;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        if (ctx.AllTargets.Count == 0) return;

        foreach (var target in ctx.AllTargets)
        {
            var tc = new DeploymentTargetContext { Machine = target };

            await LoadTransportForTargetAsync(tc, ct).ConfigureAwait(false);

            if (tc.Transport != null)
                await ValidateAccountScopeAsync(ctx, tc, ct).ConfigureAwait(false);

            ContributeEndpointVariablesForTarget(tc);

            if (tc.Transport != null)
                await ContributeAdditionalVariablesForTargetAsync(ctx, tc, ct).ConfigureAwait(false);

            ctx.AllTargetsContext.Add(tc);
        }
    }

    private async Task LoadTransportForTargetAsync(DeploymentTargetContext tc, CancellationToken ct)
    {
        tc.CommunicationStyle = CommunicationStyleParser.Parse(tc.Machine.Endpoint);
        tc.Transport = transportRegistry.Resolve(tc.CommunicationStyle);

        if (tc.Transport != null)
            tc.EndpointContext = await endpointContextBuilder.BuildAsync(tc.Machine.Endpoint, tc.Transport.Variables, ct).ConfigureAwait(false);
        else
            tc.EndpointContext = new EndpointContext { EndpointJson = tc.Machine.Endpoint };

        tc.EndpointContext.MachineId = tc.Machine.Id;
    }

    private async Task ValidateAccountScopeAsync(DeploymentTaskContext ctx, DeploymentTargetContext tc, CancellationToken ct)
    {
        var refs = tc.Transport.Variables.ParseResourceReferences(tc.EndpointContext.EndpointJson);
        var accountId = refs.FindFirst(EndpointResourceType.AuthenticationAccount);
        if (!accountId.HasValue) return;

        var account = await deploymentAccountDataProvider.GetAccountByIdAsync(accountId.Value, ct).ConfigureAwait(false);

        if (account != null)
            ValidateAccountEnvironmentScope(account, ctx.Environment);
    }

    internal static void ValidateAccountEnvironmentScope(DeploymentAccount account, Persistence.Entities.Deployments.Environment environment)
    {
        if (string.IsNullOrEmpty(account.EnvironmentIds)) return;

        List<int> scopedIds;
        try { scopedIds = JsonSerializer.Deserialize<List<int>>(account.EnvironmentIds); }
        catch { return; }

        if (scopedIds == null || scopedIds.Count == 0) return;

        if (!scopedIds.Contains(environment.Id))
            throw new AccountEnvironmentScopeException(account.Id, account.Name, environment.Id);
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
