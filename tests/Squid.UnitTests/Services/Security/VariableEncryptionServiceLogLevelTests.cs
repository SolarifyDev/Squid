using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Shouldly;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;
using Xunit;

namespace Squid.UnitTests.Services.Security;

/// <summary>
/// P1-B.5 (Phase-7, post-Phase-5 deep audit): the per-call success log was
/// emitted at <c>Information</c> level, fired on EVERY encrypt / decrypt.
/// At Information that line lands in Seq with a high-frequency timestamp
/// pattern that lets an observer with read-only Seq access infer:
/// <list type="bullet">
///   <item>Which VariableSets are being touched and how often (privacy
///         leak by metadata).</item>
///   <item>Timing-sidechannel signal for cryptographic operations
///         (per-byte latency through aggregated timestamps).</item>
/// </list>
///
/// <para>Fix: drop the success line to <c>Debug</c>. Default Seq-Information
/// configurations no longer ingest it; operators who want the trail can
/// raise the level explicitly. Failure / warning paths stay at
/// <c>Warning</c> / <c>Error</c> as they were.</para>
/// </summary>
public sealed class VariableEncryptionServiceLogLevelTests
{
    [Fact]
    public void Encrypt_Success_LogsAtDebug_NotInformation()
    {
        // Note: EncryptAsync is misnamed — it's actually synchronous (returns
        // string, not Task<string>). B.4 in the audit tracks the rename;
        // this test only addresses the log-level half of the privacy issue.
        var (sink, restore) = InstallCapturingLogger();
        try
        {
            var service = MakeService();

            service.EncryptAsync("hello-secret", variableSetId: 42);

            var successEvents = sink.Events
                .Where(e => e.RenderMessage().Contains("Successfully encrypted"))
                .ToList();

            successEvents.Count.ShouldBeGreaterThan(0,
                customMessage: "the success path should still emit a structured event — just at Debug.");

            successEvents.ShouldAllBe(e => e.Level == LogEventLevel.Debug,
                customMessage:
                    "B.5 — per-call encrypt success must NOT log at Information. " +
                    "Information lands in default Seq pipelines and exposes per-VariableSet " +
                    "frequency / timing metadata to anyone with Seq read access.");
        }
        finally { restore(); }
    }

    [Fact]
    public async Task Decrypt_Success_LogsAtDebug_NotInformation()
    {
        var (sink, restore) = InstallCapturingLogger();
        try
        {
            var service = MakeService();
            var encrypted = service.EncryptAsync("hello-secret", variableSetId: 42);
            sink.Events.Clear();   // we only care about the decrypt event

            await service.DecryptAsync(encrypted, variableSetId: 42);

            var successEvents = sink.Events
                .Where(e => e.RenderMessage().Contains("Successfully decrypted"))
                .ToList();

            successEvents.Count.ShouldBeGreaterThan(0);
            successEvents.ShouldAllBe(e => e.Level == LogEventLevel.Debug,
                customMessage: "decrypt success must also drop to Debug; same exposure surface as encrypt.");
        }
        finally { restore(); }
    }

    [Fact]
    public void Encrypt_NeverEmitsValueInLogPayload()
    {
        // Belt-and-braces: even before the level change, the success template
        // must NEVER include the plaintext or ciphertext value. Pin it.
        var (sink, restore) = InstallCapturingLogger();
        try
        {
            var service = MakeService();

            service.EncryptAsync("super-secret-pw-123", variableSetId: 42);

            foreach (var ev in sink.Events)
            {
                var rendered = ev.RenderMessage();
                rendered.ShouldNotContain("super-secret-pw-123",
                    customMessage: "encryption log payload must NEVER echo the plaintext value.");
            }
        }
        finally { restore(); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static VariableEncryptionService MakeService()
    {
        // Realistic 32-byte base64 key — passes Strict mode validation.
        var keyBytes = new byte[32];
        for (var i = 0; i < keyBytes.Length; i++) keyBytes[i] = (byte)(i + 1);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Security:VariableEncryption:MasterKey"] = Convert.ToBase64String(keyBytes)
            })
            .Build();
        var setting = new SecuritySetting(configuration);
        return new VariableEncryptionService(setting);
    }

    private static (CapturingLogSink Sink, Action Restore) InstallCapturingLogger()
    {
        var original = Log.Logger;
        var sink = new CapturingLogSink();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()   // capture Debug too
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (sink, () => Log.Logger = original);
    }

    private sealed class CapturingLogSink : Serilog.Core.ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
