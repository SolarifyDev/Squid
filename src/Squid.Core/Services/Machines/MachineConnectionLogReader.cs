using System.Linq;
using Halibut.Diagnostics;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Services.Machines;

/// <summary>
/// Reads the REAL Halibut connection log for a machine and projects it into the
/// API DTO. Composes the singleton Halibut <see cref="ILogFactory"/> (wired into
/// the server runtime in <c>HalibutModule</c> so it accumulates per-endpoint
/// connection events) with the machine's endpoint URI, resolved the same way the
/// health-check + dispatch paths resolve it — so the URI we read matches the key
/// Halibut wrote under.
///
/// <para>The Halibut event type never leaks past this reader — callers get the
/// transport-agnostic <see cref="MachineConnectionLogEntry"/> shape.</para>
/// </summary>
public interface IMachineConnectionLogReader
{
    Task<GetMachineConnectionLogResponseData> ReadAsync(int machineId, int maxEntries, CancellationToken ct);
}

public sealed class MachineConnectionLogReader : IMachineConnectionLogReader, IScopedDependency
{
    private readonly IMachineDataProvider _machineDataProvider;
    private readonly ILogFactory _connectionLogFactory;

    public MachineConnectionLogReader(IMachineDataProvider machineDataProvider, ILogFactory connectionLogFactory)
    {
        _machineDataProvider = machineDataProvider;
        _connectionLogFactory = connectionLogFactory;
    }

    public async Task<GetMachineConnectionLogResponseData> ReadAsync(int machineId, int maxEntries, CancellationToken ct)
    {
        var machine = await _machineDataProvider.GetMachinesByIdAsync(machineId, ct).ConfigureAwait(false);

        var data = new GetMachineConnectionLogResponseData { MachineId = machineId };

        var endpointUri = machine == null ? null : EndpointJsonHelper.ResolveConnectionEndpointUri(machine.Endpoint);

        if (endpointUri == null) return data;

        data.Endpoint = endpointUri.ToString();
        data.Entries = ReadRecentEntries(endpointUri, maxEntries);

        return data;
    }

    private List<MachineConnectionLogEntry> ReadRecentEntries(Uri endpointUri, int maxEntries)
    {
        var events = _connectionLogFactory.ForEndpoint(endpointUri).GetLogs() ?? new List<LogEvent>();

        var skip = Math.Max(0, events.Count - maxEntries);

        return events.Skip(skip).Select(Project).ToList();
    }

    private static MachineConnectionLogEntry Project(LogEvent ev) => new()
    {
        OccurredAt = ev.Time,
        Type = ev.Type.ToString(),
        Message = ev.FormattedMessage ?? string.Empty,
        Error = ev.Error?.Message ?? string.Empty
    };
}
