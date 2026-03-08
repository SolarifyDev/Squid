using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution;

public partial class DeploymentTaskExecutor
{
    private async Task LoadDeploymentDataAsync(CancellationToken ct)
    {
        await LoadDeploymentAsync(ct).ConfigureAwait(false);
        await LoadSelectedPackagesAsync(ct).ConfigureAwait(false);
        await LoadOrSnapshotAsync(ct).ConfigureAwait(false);
        await ResolveVariablesAsync(ct).ConfigureAwait(false);
        await FindTargetsAsync(ct).ConfigureAwait(false);

        ConvertSnapshotToSteps();
        PreFilterTargetsByRoles();
    }

    private async Task LogDeploymentDataSummaryAsync(int serverTaskId, CancellationToken ct)
    {
        await LogMachineSelectionConstraintsAsync(serverTaskId, ct).ConfigureAwait(false);
        await PersistTaskLogAsync(serverTaskId, ServerTaskLogCategory.Info, $"Found {_ctx.AllTargets.Count} targets: {string.Join(", ", _ctx.AllTargets.Select(t => t.Name))}", "System", _ctx.TaskActivityNode?.Id, ct).ConfigureAwait(false);
    }

    private async Task PrepareAllTargetsAsync(CancellationToken ct)
    {
        foreach (var target in _ctx.AllTargets)
        {
            var tc = new DeploymentTargetContext { Machine = target };

            LoadTransportForTarget(tc);

            await PersistTaskLogAsync(_ctx.Task.Id, ServerTaskLogCategory.Info, $"Preparing target: {target.Name} ({tc.CommunicationStyle})", "System", _ctx.TaskActivityNode?.Id, ct).ConfigureAwait(false);

            if (tc.Transport == null)
            {
                await PersistTaskLogAsync(_ctx.Task.Id, ServerTaskLogCategory.Warning, $"No transport resolved for target {target.Name} with style {tc.CommunicationStyle}", "System", _ctx.TaskActivityNode?.Id, ct).ConfigureAwait(false);
            }

            if (tc.Transport != null)
                await LoadAuthenticationAsync(tc, ct).ConfigureAwait(false);

            ContributeEndpointVariablesForTarget(tc);

            if (tc.Transport != null)
                await ContributeAdditionalVariablesForTargetAsync(tc, ct).ConfigureAwait(false);

            _ctx.AllTargetsContext.Add(tc);
        }
    }

    private async Task LoadTaskAsync(int serverTaskId, CancellationToken ct)
    {
        var task = await _serverTaskService.StartExecutingAsync(serverTaskId, ct).ConfigureAwait(false);

        _ctx.Task = task;

        Log.Information("Start processing task {TaskId}", serverTaskId);
    }

    private async Task LoadDeploymentAsync(CancellationToken ct)
    {
        var deployment = await _deploymentDataProvider.GetDeploymentByTaskIdAsync(_ctx.Task.Id, ct).ConfigureAwait(false);

        if (deployment == null) throw new DeploymentEntityNotFoundException("Deployment", $"task:{_ctx.Task.Id}");

        _ctx.Deployment = deployment;
        _ctx.Deployment.DeploymentRequestPayload = DeploymentTargetFinder.ParseRequestPayload(deployment.Json);

        var release = await _releaseDataProvider.GetReleaseByIdAsync(deployment.ReleaseId, ct).ConfigureAwait(false);
        var project = await _projectDataProvider.GetProjectByIdAsync(deployment.ProjectId, ct).ConfigureAwait(false);
        var environment = await _environmentDataProvider.GetEnvironmentByIdAsync(deployment.EnvironmentId, ct).ConfigureAwait(false);
        
        _ctx.Release = release;
        _ctx.Project = project;
        _ctx.Environment = environment;
    }

    private async Task LoadSelectedPackagesAsync(CancellationToken ct)
    {
        _ctx.SelectedPackages = await _releaseSelectedPackageDataProvider
            .GetByReleaseIdAsync(_ctx.Release.Id, ct).ConfigureAwait(false);

        Log.Information("Loaded {Count} selected packages for release {ReleaseId}", _ctx.SelectedPackages.Count, _ctx.Release.Id);
    }

    private async Task LoadOrSnapshotAsync(CancellationToken ct)
    {
        Log.Information("Loading process snapshot for deployment {DeploymentId}", _ctx.Deployment.Id);

        if (_ctx.Deployment.ProcessSnapshotId.HasValue)
        {
            _ctx.ProcessSnapshot = await _snapshotService.LoadProcessSnapshotAsync(_ctx.Deployment.ProcessSnapshotId.Value, ct).ConfigureAwait(false);

            return;
        }

        _ctx.ProcessSnapshot = await _snapshotService.SnapshotProcessFromReleaseAsync(_ctx.Release, ct).ConfigureAwait(false);

        _ctx.Deployment.ProcessSnapshotId = _ctx.ProcessSnapshot.Id;

        await _deploymentDataProvider.UpdateDeploymentAsync(_ctx.Deployment, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task ResolveVariablesAsync(CancellationToken ct)
    {
        Log.Information("Resolving variables for deployment {DeploymentId}", _ctx.Deployment.Id);

        _ctx.Variables = await _variableResolver.ResolveVariablesAsync(_ctx.Deployment.Id, ct).ConfigureAwait(false);

        _ctx.Variables.Add(new VariableDto { Name = DeploymentVariableNames.DeploymentId, Value = _ctx.Deployment.Id.ToString() });
    }

    private async Task LogMachineSelectionConstraintsAsync(int serverTaskId, CancellationToken ct)
    {
        var selection = DeploymentTargetFinder.ParseTargetSelection(_ctx.Deployment?.Json);
        if (!selection.HasConstraints)
            return;

        var includeText = selection.SpecificMachineIds.Count == 0
            ? "*"
            : string.Join(", ", selection.SpecificMachineIds.OrderBy(x => x));
        var excludeText = selection.ExcludedMachineIds.Count == 0
            ? "-"
            : string.Join(", ", selection.ExcludedMachineIds.OrderBy(x => x));

        await PersistTaskLogAsync(serverTaskId, ServerTaskLogCategory.Info, $"Machine selection constraints applied. Include: {includeText}; Exclude: {excludeText}", "System", _ctx.TaskActivityNode?.Id, ct).ConfigureAwait(false);
    }

    private async Task FindTargetsAsync(CancellationToken ct)
    {
        Log.Information("Finding targets for deployment {DeploymentId}", _ctx.Deployment.Id);

        _ctx.AllTargets = await _targetFinder.FindTargetsAsync(_ctx.Deployment, ct).ConfigureAwait(false);

        if (_ctx.AllTargets.Count == 0) throw new DeploymentTargetException($"No target machines found for deployment {_ctx.Deployment.Id}", _ctx.Deployment.Id);

        Log.Information("Found {Count} target machines for deployment {DeploymentId}", _ctx.AllTargets.Count, _ctx.Deployment.Id);
    }

    private void LoadTransportForTarget(DeploymentTargetContext tc)
    {
        tc.EndpointContext = new EndpointContext { EndpointJson = tc.Machine.Endpoint };
        tc.CommunicationStyle = CommunicationStyleParser.Parse(tc.EndpointContext.EndpointJson);
        tc.Transport = _transportRegistry.Resolve(tc.CommunicationStyle);
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
        var account = await _deploymentAccountDataProvider.GetAccountByIdAsync(accountId, ct).ConfigureAwait(false);

        if (account == null) return;

        tc.EndpointContext.SetAccountData(account.AccountType, account.Credentials);
    }

    private async Task ResolveCertificateAsync(DeploymentTargetContext tc, EndpointResourceReference certRef, CancellationToken ct)
    {
        var cert = await _certificateDataProvider.GetCertificateByIdAsync(certRef.ResourceId, ct).ConfigureAwait(false);

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

    private void ContributeEndpointVariablesForTarget(DeploymentTargetContext tc)
    {
        if (tc.Transport == null) return;

        var endpointVars = tc.Transport.Variables.ContributeVariables(tc.EndpointContext);

        tc.EndpointVariables.AddRange(endpointVars);
    }

    private async Task ContributeAdditionalVariablesForTargetAsync(DeploymentTargetContext tc, CancellationToken ct)
    {
        var additionalVars = await tc.Transport.Variables
            .ContributeAdditionalVariablesAsync(_ctx.ProcessSnapshot, _ctx.Release, ct).ConfigureAwait(false);

        tc.EndpointVariables.AddRange(additionalVars);
    }

    private void PreFilterTargetsByRoles()
    {
        var allRoles = DeploymentTargetFinder.CollectAllTargetRoles(_ctx.Steps);

        if (allRoles.Count == 0)
            return;

        var before = _ctx.AllTargets.Count;

        _ctx.AllTargets = DeploymentTargetFinder.FilterByRoles(_ctx.AllTargets, allRoles);

        if (_ctx.AllTargets.Count < before)
            Log.Information("Pre-filtered targets by roles: {Before} → {After} (roles: {Roles})", before, _ctx.AllTargets.Count, string.Join(", ", allRoles));

        if (_ctx.AllTargets.Count == 0)
            throw new DeploymentTargetException($"No target machines match the required roles [{string.Join(", ", allRoles)}] for deployment {_ctx.Deployment.Id}", _ctx.Deployment.Id);
    }

    private void ConvertSnapshotToSteps()
    {
        _ctx.Steps = ConvertProcessSnapshotToSteps(_ctx.ProcessSnapshot);
    }

    public static List<DeploymentStepDto> ConvertProcessSnapshotToSteps(DeploymentProcessSnapshotDto processSnapshot) => ProcessSnapshotStepConverter.Convert(processSnapshot);
}
