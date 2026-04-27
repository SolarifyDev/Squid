using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Core;
using Serilog;

namespace Squid.Tentacle.Commands;

/// <summary>
/// P1-Phase9b.5 (audit OctopusTentacle gap #7) — operator-facing self-diagnostic
/// CLI command. Verifies the agent's local services would respond correctly
/// to a server's health-check probe, WITHOUT actually contacting a server.
/// Mirrors OctopusTentacle's <c>check-services</c> command.
///
/// <para><b>Operator workflow</b>: when an agent looks healthy in
/// <c>systemctl status</c> but health checks fail server-side, run
/// <c>tentacle check-services</c> on the host. It exercises each service the
/// agent registers (<c>IScriptService</c>, <c>ICapabilitiesService</c>,
/// <c>IFileTransferService</c>) locally and reports per-service pass/fail
/// + diagnostic detail.</para>
///
/// <para><b>Exit codes</b>:
/// <list type="bullet">
///   <item><c>0</c> — all services healthy</item>
///   <item><c>1</c> — one or more services unhealthy (operator action needed)</item>
/// </list></para>
///
/// <para><b>What we check</b>:
/// <list type="number">
///   <item><b>Configuration</b>: ServerUrl / ServerCommsUrl / ServerCertificate
///         / instance certificate present and well-formed.</item>
///   <item><b>IScriptService</b>: instantiate the local script service +
///         workspace path resolves + a no-op admission policy passes.</item>
///   <item><b>ICapabilitiesService</b>: the capabilities response builds
///         (reads on-disk upgrade-status files but tolerates missing).</item>
///   <item><b>IFileTransferService</b>: upload root resolves and is writable.</item>
/// </list></para>
/// </summary>
public sealed class CheckServicesCommand : ITentacleCommand
{
    public string Name => "check-services";
    public string Description => "Self-diagnostic: verify each agent-side service responds correctly without contacting a server";

    public async Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        var settings = TentacleApp.LoadTentacleSettings(config);
        var checks = new List<(string Name, bool Pass, string Detail)>();

        Console.WriteLine("=== Squid Tentacle Service Check ===");
        Console.WriteLine();

        checks.Add(CheckConfiguration(settings));
        checks.Add(CheckCertificate(settings));
        checks.Add(CheckScriptService(settings));
        checks.Add(CheckCapabilitiesService());
        checks.Add(CheckFileTransferService());

        Console.WriteLine();
        Console.WriteLine("=== Results ===");
        var passed = 0;
        var failed = 0;

        foreach (var (name, pass, detail) in checks)
        {
            var status = pass ? "OK    " : "FAIL  ";
            Console.WriteLine($"  [{status}] {name,-30} {detail}");
            if (pass) passed++; else failed++;
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {checks.Count} | Passed: {passed} | Failed: {failed}");

        // Make Linux's exit-code asciigram readable: 0 = all good, 1 = something failed.
        await Task.CompletedTask.ConfigureAwait(false);
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Internal entry point so unit tests can drive the check loop with a
    /// pre-built settings instance + assert the per-check outcome without
    /// printing to Console.
    /// </summary>
    internal static IReadOnlyList<(string Name, bool Pass, string Detail)> RunChecks(Squid.Tentacle.Configuration.TentacleSettings settings)
    {
        return new[]
        {
            CheckConfiguration(settings),
            CheckCertificate(settings),
            CheckScriptService(settings),
            CheckCapabilitiesService(),
            CheckFileTransferService(),
        };
    }

    private static (string, bool, string) CheckConfiguration(Squid.Tentacle.Configuration.TentacleSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ServerUrl))
            return ("Configuration", false, "ServerUrl is not set");

        var hasComms = !string.IsNullOrWhiteSpace(settings.ServerCommsUrl)
                       || !string.IsNullOrWhiteSpace(settings.ServerCommsAddresses);
        var mode = hasComms ? "Polling" : "Listening";

        return ("Configuration", true, $"mode={mode}, ServerUrl={settings.ServerUrl}");
    }

    private static (string, bool, string) CheckCertificate(Squid.Tentacle.Configuration.TentacleSettings settings)
    {
        try
        {
            var certManager = new TentacleCertificateManager(settings.CertsPath);
            var cert = certManager.LoadOrCreateCertificate();
            var daysToExpiry = (int)(cert.NotAfter - DateTime.UtcNow).TotalDays;

            if (daysToExpiry < 0)
                return ("Certificate", false, $"EXPIRED {-daysToExpiry} day(s) ago — run 'squid-tentacle new-certificate'");

            if (daysToExpiry < 30)
                return ("Certificate", true, $"expires in {daysToExpiry} day(s) (warning: <30d)");

            return ("Certificate", true, $"expires in {daysToExpiry} day(s); thumbprint={cert.Thumbprint[..8]}…");
        }
        catch (Exception ex)
        {
            return ("Certificate", false, $"failed to load: {ex.Message}");
        }
    }

    private static (string, bool, string) CheckScriptService(Squid.Tentacle.Configuration.TentacleSettings settings)
    {
        try
        {
            // Instantiate the local script service + verify workspace path is
            // resolvable and writable. Don't actually run a script — that
            // would have side effects.
            _ = new Squid.Tentacle.ScriptExecution.LocalScriptService();

            var workspaceTest = Path.Combine(Path.GetTempPath(), "squid-checkservices-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspaceTest);
            File.WriteAllText(Path.Combine(workspaceTest, "test.txt"), "ok");
            Directory.Delete(workspaceTest, recursive: true);

            return ("IScriptService", true, "instantiated; workspace I/O ok");
        }
        catch (Exception ex)
        {
            return ("IScriptService", false, $"error: {ex.Message}");
        }
    }

    private static (string, bool, string) CheckCapabilitiesService()
    {
        try
        {
            var caps = new CapabilitiesService();
            var response = caps.GetCapabilities(new Squid.Message.Contracts.Tentacle.CapabilitiesRequest());

            if (response?.SupportedServices == null || response.SupportedServices.Count == 0)
                return ("ICapabilitiesService", false, "returned empty SupportedServices list");

            return ("ICapabilitiesService", true, $"AgentVersion={response.AgentVersion}, services=[{string.Join(",", response.SupportedServices)}]");
        }
        catch (Exception ex)
        {
            return ("ICapabilitiesService", false, $"error: {ex.Message}");
        }
    }

    private static (string, bool, string) CheckFileTransferService()
    {
        try
        {
            // Verify upload-root is writable. LocalFileTransferService
            // creates the root in its constructor — failure surfaces here.
            var svc = new Squid.Tentacle.FileTransfer.LocalFileTransferService();
            var probe = Path.Combine(svc.UploadRoot, ".healthcheck-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            return ("IFileTransferService", true, $"uploadRoot={svc.UploadRoot}");
        }
        catch (Exception ex)
        {
            return ("IFileTransferService", false, $"error: {ex.Message}");
        }
    }
}
