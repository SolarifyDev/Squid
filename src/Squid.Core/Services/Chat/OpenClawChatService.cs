using System.Threading.Channels;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Http;
using Squid.Core.Services.Http.Clients;
using Squid.Core.Services.Machines;
using Squid.Message.Commands.Chat;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Chat;

public interface IOpenClawChatService : IScopedDependency
{
    Task<List<OpenClawChatResultItem>> SendAsync(SendOpenClawChatCommand command, CancellationToken ct);

    IAsyncEnumerable<OpenClawChatStreamEvent> StreamAsync(SendOpenClawChatCommand command, CancellationToken ct);
}

public class OpenClawChatService : IOpenClawChatService
{
    private readonly IMachineDataProvider _machineDataProvider;
    private readonly IDeploymentAccountDataProvider _accountDataProvider;
    private readonly OpenClawClient _client;

    public OpenClawChatService(IMachineDataProvider machineDataProvider, IDeploymentAccountDataProvider accountDataProvider, ISquidHttpClientFactory httpClientFactory)
    {
        _machineDataProvider = machineDataProvider;
        _accountDataProvider = accountDataProvider;
        _client = new OpenClawClient(httpClientFactory);
    }

    // === Non-streaming ===

    public async Task<List<OpenClawChatResultItem>> SendAsync(SendOpenClawChatCommand command, CancellationToken ct)
    {
        var machines = await ResolveMachinesAsync(command.TargetTags, ct).ConfigureAwait(false);
        var tasks = machines.Select(m => ExecuteForMachineAsync(m, command, ct)).ToList();

        return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
    }

    private async Task<OpenClawChatResultItem> ExecuteForMachineAsync(Machine machine, SendOpenClawChatCommand command, CancellationToken ct)
    {
        try
        {
            var request = await BuildChatRequestAsync(machine, command, ct).ConfigureAwait(false);

            using var cts = new CancellationTokenSource(request.Timeout);
            var response = await _client.ChatCompletionAsync(request, cts.Token).ConfigureAwait(false);

            return new OpenClawChatResultItem
            {
                MachineId = machine.Id,
                MachineName = machine.Name,
                Succeeded = response.Ok,
                Content = response.Content,
                Model = response.Model,
                FinishReason = response.FinishReason,
                Error = response.Error
            };
        }
        catch (Exception ex)
        {
            return new OpenClawChatResultItem
            {
                MachineId = machine.Id,
                MachineName = machine.Name,
                Succeeded = false,
                Error = ex.Message
            };
        }
    }

    // === Streaming ===

    public IAsyncEnumerable<OpenClawChatStreamEvent> StreamAsync(SendOpenClawChatCommand command, CancellationToken ct)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<OpenClawChatStreamEvent>();

        _ = RunStreamPipelineAsync(command, channel.Writer, ct);

        return channel.Reader.ReadAllAsync(ct);
    }

    private async Task RunStreamPipelineAsync(SendOpenClawChatCommand command, ChannelWriter<OpenClawChatStreamEvent> writer, CancellationToken ct)
    {
        try
        {
            var machines = await ResolveMachinesAsync(command.TargetTags, ct).ConfigureAwait(false);
            var tasks = machines.Select(m => StreamForMachineAsync(m, command, writer, ct)).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            writer.Complete();
        }
    }

    private async Task StreamForMachineAsync(Machine machine, SendOpenClawChatCommand command, ChannelWriter<OpenClawChatStreamEvent> writer, CancellationToken ct)
    {
        try
        {
            var request = await BuildChatRequestAsync(machine, command, ct).ConfigureAwait(false);

            await foreach (var chunk in _client.ChatCompletionStreamAsync(request, ct).ConfigureAwait(false))
            {
                await writer.WriteAsync(new OpenClawChatStreamEvent
                {
                    MachineId = machine.Id,
                    MachineName = machine.Name,
                    Delta = chunk.Delta,
                    Model = chunk.Model,
                    FinishReason = chunk.FinishReason
                }, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await writer.WriteAsync(new OpenClawChatStreamEvent
            {
                MachineId = machine.Id,
                MachineName = machine.Name,
                Error = ex.Message
            }, ct).ConfigureAwait(false);
        }
    }

    // === Shared ===

    internal async Task<List<Machine>> ResolveMachinesAsync(List<string> targetTags, CancellationToken ct)
    {
        var all = await _machineDataProvider.GetMachinesByFilterAsync(new HashSet<int>(), new HashSet<string>(), ct).ConfigureAwait(false);

        var machines = all.Where(m => !m.IsDisabled).ToList();
        machines = FilterOpenClawMachines(machines);

        if (targetTags != null && targetTags.Count > 0)
            machines = DeploymentTargetFinder.FilterByRoles(machines, targetTags.ToHashSet(StringComparer.OrdinalIgnoreCase));

        return machines;
    }

    private async Task<OpenClawChatRequest> BuildChatRequestAsync(Machine machine, SendOpenClawChatCommand command, CancellationToken ct)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<OpenClawEndpointDto>(machine.Endpoint);

        if (endpoint == null || string.IsNullOrEmpty(endpoint.BaseUrl))
            throw new InvalidOperationException($"Machine {machine.Name} has invalid OpenClaw endpoint");

        var gatewayToken = await ResolveGatewayTokenAsync(endpoint, ct).ConfigureAwait(false);

        var messages = command.Messages.Select(m => new OpenClawChatMessage(m.Role, m.Content)).ToList();

        return new OpenClawChatRequest(endpoint.BaseUrl, gatewayToken, messages, command.Model, command.ModelOverride, command.SessionKey, command.AgentId, command.Channel, command.User, TimeSpan.FromSeconds(command.TimeoutSeconds), command.Temperature, command.MaxTokens, command.ResponseFormat);
    }

    private async Task<string> ResolveGatewayTokenAsync(OpenClawEndpointDto endpoint, CancellationToken ct)
    {
        var refs = new EndpointResourceReferences { References = endpoint.ResourceReferences ?? new() };
        var accountId = refs.FindFirst(EndpointResourceType.AuthenticationAccount);

        if (accountId == null)
            return endpoint.InlineGatewayToken ?? string.Empty;

        var account = await _accountDataProvider.GetAccountByIdAsync(accountId.Value, ct).ConfigureAwait(false);

        if (account == null)
            return endpoint.InlineGatewayToken ?? string.Empty;

        var credentials = DeploymentAccountCredentialsConverter.Deserialize(account.AccountType, account.Credentials);

        if (credentials is OpenClawGatewayCredentials oc && !string.IsNullOrEmpty(oc.GatewayToken))
            return oc.GatewayToken;

        if (credentials is TokenCredentials tc && !string.IsNullOrEmpty(tc.Token))
            return tc.Token;

        return endpoint.InlineGatewayToken ?? string.Empty;
    }

    private static List<Machine> FilterOpenClawMachines(List<Machine> machines)
    {
        return machines.Where(m =>
        {
            var endpoint = EndpointVariableFactory.TryDeserialize<OpenClawEndpointDto>(m.Endpoint);
            return endpoint?.CommunicationStyle == nameof(CommunicationStyle.OpenClaw);
        }).ToList();
    }
}
