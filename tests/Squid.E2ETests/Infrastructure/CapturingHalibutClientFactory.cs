using Halibut;
using Squid.Core.Commands.Tentacle;
using Squid.Core.Services.Deployments;
using Squid.Core.Services.Tentacle;

namespace Squid.E2ETests.Infrastructure;

public class CapturingHalibutClientFactory : IHalibutClientFactory
{
    public List<StartScriptCommand> CapturedCommands { get; } = new();
    public Dictionary<string, byte[]> CapturedFileBytes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => new CapturingScriptService(this);

    private sealed class CapturingScriptService : IAsyncScriptService
    {
        private readonly CapturingHalibutClientFactory _factory;

        public CapturingScriptService(CapturingHalibutClientFactory factory)
            => _factory = factory;

        public Task<ScriptTicket> StartScriptAsync(StartScriptCommand command)
        {
            _factory.CapturedCommands.Add(command);
            CaptureFiles(command);
            return Task.FromResult(new ScriptTicket(Guid.NewGuid().ToString("N")));
        }

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
        {
            return Task.FromResult(new ScriptStatusResponse(
                request.Ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0));
        }

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command)
        {
            return Task.FromResult(new ScriptStatusResponse(
                command.Ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0));
        }

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command)
        {
            return Task.FromResult(new ScriptStatusResponse(
                command.Ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0));
        }

        private void CaptureFiles(StartScriptCommand command)
        {
            foreach (var file in command.Files)
            {
                var tempPath = Path.GetTempFileName();

                try
                {
                    file.Contents.Receiver().SaveToAsync(tempPath, CancellationToken.None)
                        .GetAwaiter().GetResult();

                    _factory.CapturedFileBytes[file.Name] = File.ReadAllBytes(tempPath);
                }
                catch
                {
                    // Best effort capture — file may not be extractable
                }
                finally
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
            }
        }
    }
}
