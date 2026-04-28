using System.Collections.Generic;
using Shouldly;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// P0-Phase10.2 (audit C.2) — pin the "no secrets in helm argv" contract.
///
/// <para><b>The bug pre-Phase-10.2</b>: <c>HelmUpgradeScriptBuilder</c>
/// emitted <c>--set "key=value"</c> for inline values. The full value text
/// landed in the helm process argv → visible to:
/// <list type="bullet">
///   <item><c>ps aux</c> on the host (any local user can read)</item>
///   <item><c>/proc/&lt;pid&gt;/cmdline</c> (kernel-exposed)</item>
///   <item>kubelet logs when helm is invoked from a pod</item>
///   <item>audit logs (Sysdig/Falco/cloud audit)</item>
///   <item>Bash history / <c>set -x</c> traces in failure-debugging</item>
/// </list>
/// For ANY value an operator marks as sensitive (DB password, API key,
/// TLS private key), this is unacceptable secret exposure.</para>
///
/// <para><b>The fix</b>: project ALL inline values to a generated YAML
/// values file passed via <c>--values</c>. The file lives under the
/// per-task workspace (already 0700 perms), is cleaned up after deploy
/// (existing finally-block), and never appears in argv. <c>--values</c>
/// load order means the generated file goes LAST so it overrides earlier
/// <c>--values</c> files (matches the pre-fix <c>--set</c> precedence).</para>
/// </summary>
public sealed class HelmUpgradeScriptBuilderSecretSafetyTests
{
    private static readonly string SecretValue = "super-secret-DB-password-12345!";

    // ── Bash: secrets MUST NOT land in argv via --set ────────────────────────

    [Fact]
    public void Bash_InlineValues_NotEmittedAsSetFlag()
    {
        var intent = new HelmUpgradeIntent
        {
            Name = "helm-upgrade",
            ReleaseName = "myapp",
            ChartReference = "stable/myapp",
            InlineValues = new Dictionary<string, string>
            {
                ["database.password"] = SecretValue,
                ["api.token"] = "another-secret"
            }
        };

        var script = HelmUpgradeScriptBuilder.Build(intent, ScriptSyntax.Bash);

        // The fix: NO --set with the secret value text in argv.
        script.ShouldNotContain(SecretValue, customMessage:
            "Sensitive value MUST NOT appear in helm argv (--set leaks to ps/kubelet/logs).");
        script.ShouldNotContain("another-secret");

        // The script SHOULD reference the generated values file via --values.
        script.ShouldContain("--values",
            customMessage: "Inline values must be projected to a --values file, not --set.");
    }

    [Fact]
    public void Bash_InlineValues_GeneratedYamlFile_Returned()
    {
        var intent = new HelmUpgradeIntent
        {
            Name = "helm-upgrade",
            ReleaseName = "myapp",
            ChartReference = "stable/myapp",
            InlineValues = new Dictionary<string, string>
            {
                ["database.password"] = SecretValue
            }
        };

        var files = HelmUpgradeScriptBuilder.BuildDeploymentFiles(intent);

        // A generated file with the secret content must be emitted so the
        // server can ship it to the workspace alongside any user-supplied
        // values files. Per-task workspace dir is 0700 (Phase-9.2 invariant).
        files.Count.ShouldBe(1);
        var generated = System.Linq.Enumerable.First(files, f => f.RelativePath.Contains("inline"));
        var content = System.Text.Encoding.UTF8.GetString(generated.Content);
        content.ShouldContain(SecretValue,
            customMessage: "The secret must end up in the generated YAML file (not in argv).");
    }

    [Fact]
    public void Bash_InlineAndUserSuppliedFiles_GeneratedGoesLastForPrecedence()
    {
        // Helm --values precedence: later files override earlier ones.
        // pre-Phase-10.2 --set always overrode --values; the generated
        // inline-values file must take its place at the END of the
        // --values chain to preserve override semantics.
        var userYaml = System.Text.Encoding.UTF8.GetBytes("database:\n  password: from-base\n");
        var intent = new HelmUpgradeIntent
        {
            Name = "helm-upgrade",
            ReleaseName = "myapp",
            ChartReference = "stable/myapp",
            ValuesFiles = new[] { DeploymentFile.Asset("base-values.yaml", userYaml) },
            InlineValues = new Dictionary<string, string>
            {
                ["database.password"] = SecretValue
            }
        };

        var script = HelmUpgradeScriptBuilder.Build(intent, ScriptSyntax.Bash);

        var basePos = script.IndexOf("base-values.yaml", StringComparison.Ordinal);
        var inlinePos = script.IndexOf("inline-values.yaml", StringComparison.Ordinal);

        basePos.ShouldBeGreaterThan(-1);
        inlinePos.ShouldBeGreaterThan(-1);
        inlinePos.ShouldBeGreaterThan(basePos, customMessage:
            "Generated inline-values.yaml must come AFTER user-supplied values files for correct override semantics.");
    }

    [Fact]
    public void Bash_NoInlineValues_NoGeneratedFile()
    {
        // Regression: when InlineValues is empty, BuildDeploymentFiles
        // must NOT emit an empty inline-values.yaml. Helm parses empty
        // YAML as {} and would log a noisy warning.
        var intent = new HelmUpgradeIntent
        {
            Name = "helm-upgrade",
            ReleaseName = "myapp",
            ChartReference = "stable/myapp",
            InlineValues = new Dictionary<string, string>()  // empty
        };

        var files = HelmUpgradeScriptBuilder.BuildDeploymentFiles(intent);

        files.Count.ShouldBe(0, customMessage:
            "Empty InlineValues must produce zero generated files (no empty yaml file).");
    }

    // ── PowerShell: same contract for the Windows path ──────────────────────

    [Fact]
    public void PowerShell_InlineValues_NotEmittedAsSetArg()
    {
        var intent = new HelmUpgradeIntent
        {
            Name = "helm-upgrade",
            ReleaseName = "myapp",
            ChartReference = "stable/myapp",
            InlineValues = new Dictionary<string, string>
            {
                ["database.password"] = SecretValue
            }
        };

        var script = HelmUpgradeScriptBuilder.Build(intent, ScriptSyntax.PowerShell);

        script.ShouldNotContain(SecretValue, customMessage:
            "Sensitive value MUST NOT appear in PowerShell argv either.");
        script.ShouldContain("--values",
            customMessage: "Inline values must be projected to --values file on PowerShell too.");
    }

    // ── YAML structure: nested keys correctly projected ─────────────────────

    [Fact]
    public void GeneratedYaml_NestedKeyPath_ProducesNestedYamlStructure()
    {
        // helm's "key=value" with dots means nested YAML structure.
        // database.password=secret → database: { password: secret }
        // The projection must preserve this — sending a flat key with
        // dots would break helm's value resolution.
        var intent = new HelmUpgradeIntent
        {
            Name = "helm-upgrade",
            ReleaseName = "myapp",
            ChartReference = "stable/myapp",
            InlineValues = new Dictionary<string, string>
            {
                ["database.password"] = "secret",
                ["database.host"] = "db.example.com",
                ["api.token"] = "tok"
            }
        };

        var files = HelmUpgradeScriptBuilder.BuildDeploymentFiles(intent);

        var generated = System.Linq.Enumerable.First(files, f => f.RelativePath.Contains("inline"));
        var yaml = System.Text.Encoding.UTF8.GetString(generated.Content);

        // Both "database" keys must be under one "database:" block, NOT two.
        var databaseCount = System.Text.RegularExpressions.Regex.Matches(yaml, @"^database:", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        databaseCount.ShouldBe(1, customMessage:
            $"database.password and database.host must merge under one nested block. YAML was:\n{yaml}");
    }
}
