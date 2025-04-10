using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Squid.Api;
using Squid.Core.Commands.Tentacle;
using Squid.Core.Services.Tentacle;
using Xunit;

namespace Squid.E2ETests.Halibut;

public class HalibutTests : IClassFixture<HalibutApiTestFixture>
{
    private readonly HttpClient _client;
    private readonly HalibutApiTestFixture _factory;

    public HalibutTests(HalibutApiTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ShouldConnectToTentacleAsync()
    {
        // var windowsScript = "curl -o \"C:\\TentacleFiles\\151351315.mov\" \"https://smartiestest.oss-cn-hongkong.aliyuncs.com/20250410/b5f397b0-b697-4754-98ed-ee0176814ca1.mov?Expires=253402300799&OSSAccessKeyId=LTAI5tEYyDT8YqJBSXaFDtyk&Signature=S6Y%2BtdiFidP8N8BrRXSxUXCyqhc%3D\"";
        // var windowsScript = "kubectl apply -f  \"C:\\TentacleFiles\\redis-apply.yaml\"";
        var windowsScript = "kubectl get pods";
        var halibut = _factory.Services.GetKeyedService<HalibutRuntime>("TestHalibutRuntime");

        var scriptClient = halibut.CreateAsyncClient<IScriptService, IAsyncScriptService>(
            new ServiceEndPoint("https://172.16.145.222:10933", "C3FD3792FFF64B4E92E5E057E94206E13C33F809", new HalibutTimeoutsAndLimits()));

        var result = await scriptClient.StartScriptAsync(new StartScriptCommand(windowsScript, ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(1), "123", [], "")).ConfigureAwait(false);
        result.TaskId.Contains("2025").ShouldBeTrue();

        var result1 = await ObserverUntilScriptOutputReceived(scriptClient, new ScriptTicket(result.TaskId), "This is the start of the script", default).ConfigureAwait(false);

        var response = await scriptClient.CompleteScriptAsync(new CompleteScriptCommand(new ScriptTicket(result.TaskId), 0)).ConfigureAwait(false);
        
        result1.State.ShouldNotBe(ProcessState.Pending);
    }

    [Fact]
    public async Task ShouldTransferFileToTentacleAsync()
    {
        var halibut = _factory.Services.GetKeyedService<HalibutRuntime>("TestHalibutRuntime");
    
        var scriptClient = halibut.CreateAsyncClient<IFileTransferService, IAsyncClientFileTransferService>(
            new ServiceEndPoint("https://172.16.145.222:10933", "C3FD3792FFF64B4E92E5E057E94206E13C33F809", new HalibutTimeoutsAndLimits()));

        var file = File.ReadAllBytes("/Users/ziruiliu/Desktop/certificate/k8s/macK8sConfig.yaml");
    
        // var result1 = await scriptClient.DownloadFileAsync("C:\\Users\\Administrator\\Desktop\\123.txt", new HalibutProxyRequestOptions(CancellationToken.None)).ConfigureAwait(false);
        var result = await scriptClient.UploadFileAsync("C:\\Users\\Administrator\\.kube\\macK8sConfig.yaml", DataStream.FromBytes(file), new HalibutProxyRequestOptions(CancellationToken.None)).ConfigureAwait(false);
        
        result.Length.ShouldNotBe(0);
    }
    
    public async Task<ScriptStatusResponse> ObserverUntilScriptOutputReceived(IAsyncScriptService scriptClient, ScriptTicket scriptTicket, string outputContains, CancellationToken cancellationToken)
    {
        var scriptStatusResponse = new ScriptStatusResponse(scriptTicket, ProcessState.Pending, 0, new List<ProcessOutput>(), 0);
        var logs = new List<ProcessOutput>();

        while (scriptStatusResponse.State != ProcessState.Complete) 
        {
            cancellationToken.ThrowIfCancellationRequested();

            scriptStatusResponse = await scriptClient.GetStatusAsync(new ScriptStatusRequest(scriptTicket, scriptStatusResponse.NextLogSequence)).ConfigureAwait(false);

            logs.AddRange(scriptStatusResponse.Logs);

            if (scriptStatusResponse.Logs.Any(l => l.Text != null && l.Text.Contains(outputContains)))
            {
                break;
            }

            if (scriptStatusResponse.State != ProcessState.Complete)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        return new ScriptStatusResponse(scriptStatusResponse.Ticket, scriptStatusResponse.State, scriptStatusResponse.ExitCode, logs, scriptStatusResponse.NextLogSequence);
    }
}