using Halibut;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Contracts.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.E2ETests.Infrastructure;

public class CapturingHalibutClientFactory : IHalibutClientFactory
{
    public List<StartScriptCommand> CapturedCommands { get; } = new();
    public Dictionary<string, byte[]> CapturedFileBytes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => new CapturingScriptService(this);

    public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => new NoOpCapabilitiesService();

    /// <summary>
    /// Test-only file-transfer client. The capturing factory is used by
    /// pipeline tests that intercept agent RPC for inspection (no real
    /// agent involvement) — file transfer is unused in those scenarios,
    /// so we throw if any test reaches this code path. A test that
    /// genuinely needs file transfer should use a fixture that wires a
    /// real <see cref="HalibutRuntime"/>, not the capturing factory.
    /// </summary>
    public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint)
        => new ThrowingFileTransferService();

    private sealed class NoOpCapabilitiesService : IAsyncCapabilitiesService
    {
        public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request)
            => Task.FromResult(new CapabilitiesResponse());
    }

    private sealed class ThrowingFileTransferService : IAsyncClientFileTransferService
    {
        private const string Message =
            "CapturingHalibutClientFactory does not support file-transfer RPC. " +
            "If a pipeline test needs to exercise file transfer, switch to a fixture " +
            "that uses a real HalibutRuntime (e.g. KubernetesAgentE2EFixture).";

        public Task<UploadResult> UploadFileAsync(string remotePath, DataStream upload)
            => throw new NotSupportedException(Message);

        public Task<DataStream> DownloadFileAsync(string remotePath)
            => throw new NotSupportedException(Message);
    }

    private sealed class CapturingScriptService : IAsyncScriptService
    {
        private readonly CapturingHalibutClientFactory _factory;

        public CapturingScriptService(CapturingHalibutClientFactory factory)
            => _factory = factory;

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command)
        {
            _factory.CapturedCommands.Add(command);
            CaptureFiles(command);
            var ticket = command.ScriptTicket ?? new ScriptTicket(Guid.NewGuid().ToString("N"));
            return Task.FromResult(new ScriptStatusResponse(
                ticket, ProcessState.Running, 0, new List<ProcessOutput>(), 0));
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
