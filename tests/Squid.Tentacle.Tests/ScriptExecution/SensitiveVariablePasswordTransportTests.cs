using System.Diagnostics;
using System.IO;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// P0-B.2 regression guard (2026-04-24 audit). Pre-fix, per-script sensitive-variable
/// encryption passwords leaked two ways:
///
/// <list type="number">
///   <item><c>WriteAdditionalFiles</c> wrote <c>{file}.key</c> next to the ciphertext
///         on disk. Encryption-at-rest was pure theatre — anyone with read access to
///         the workspace got both the ciphertext and the key. Disk snapshots / backups /
///         offline-compromise made every historical deploy's secrets recoverable.</item>
///   <item><c>StartCalamariProcess</c> passed the password via <c>--password=&lt;pw&gt;</c>
///         on the argv list. Visible in <c>ps aux</c> / <c>/proc/&lt;pid&gt;/cmdline</c>
///         which is often world-readable — so any unprivileged user on the host could
///         read every script's sensitive-vars key just by polling <c>ps</c>.</item>
/// </list>
///
/// <para>Fix:
/// <list type="bullet">
///   <item>No more <c>.key</c> file. Password never touches disk on the agent side —
///         it rides in-memory on <see cref="ScriptFile.EncryptionPassword"/>.</item>
///   <item>Password to Calamari rides on the child process's environment via
///         <see cref="LocalScriptService.CalamariSensitivePasswordEnvVar"/>. Env vars
///         are readable only by the process owner / root via
///         <c>/proc/&lt;pid&gt;/environ</c> (mode 0600) — argv is in <c>cmdline</c>
///         (mode 0444). Meaningful privacy-boundary upgrade.</item>
/// </list>
/// </para>
/// </summary>
public sealed class SensitiveVariablePasswordTransportTests
{
    [Fact]
    public void CalamariSensitivePasswordEnvVar_ConstantNamePinned()
    {
        // Renaming this env var breaks the handshake between tentacle (sets) and
        // calamari (reads). The Calamari project pins the same literal on its side —
        // drift between the two is an invisible silent failure (sensitive variables
        // simply fail to decrypt). Hard-pin so rename becomes a compile-visible decision.
        LocalScriptService.CalamariSensitivePasswordEnvVar.ShouldBe("SQUID_CALAMARI_SENSITIVE_PASSWORD");
    }

    [Fact]
    public void WriteAdditionalFiles_FileWithEncryptionPassword_DoesNotWriteKeyFile()
    {
        // The `.key` sidecar file is gone. Password lives in memory on the ScriptFile
        // instance and is transported to the Calamari process via environment variable.
        var workDir = Path.Combine(Path.GetTempPath(), $"squid-test-b2-nokey-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            var files = new List<ScriptFile>
            {
                new("sensitiveVariables.json",
                    global::Halibut.DataStream.FromBytes(new byte[] { 1, 2, 3 }),
                    encryptionPassword: "super-secret-pw")
            };

            LocalScriptService.WriteAdditionalFiles(workDir, files);

            File.Exists(Path.Combine(workDir, "sensitiveVariables.json")).ShouldBeTrue(
                customMessage: "ciphertext must still land on disk — Calamari reads it");

            File.Exists(Path.Combine(workDir, "sensitiveVariables.json.key")).ShouldBeFalse(
                customMessage:
                    "the `.key` sidecar must NOT be written. Disk co-location with the " +
                    "ciphertext defeats at-rest encryption — that's the P0-B.2 vector. " +
                    "Password rides in memory to the child process via env var.");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void BuildCalamariProcessStartInfo_SensitivePassword_UsesEnvVarNotArgv()
    {
        var psi = LocalScriptService.BuildCalamariProcessStartInfo(
            workDir: "/tmp/some-work",
            variablesPath: "/tmp/some-work/variables.json",
            sensitiveVariablesPath: "/tmp/some-work/sensitiveVariables.json",
            sensitivePassword: "super-secret-pw",
            sensitiveCiphertextExists: true,
            arguments: Array.Empty<string>());

        psi.Environment[LocalScriptService.CalamariSensitivePasswordEnvVar].ShouldBe("super-secret-pw",
            customMessage:
                "password must ride in child-process environment (readable only by process " +
                "owner / root via /proc/<pid>/environ, mode 0600)");

        psi.ArgumentList.Any(a => a.StartsWith("--password=")).ShouldBeFalse(
            customMessage:
                "no --password argv. argv appears in ps aux and /proc/<pid>/cmdline " +
                "(world-readable on most Linux distros). That's the P0-B.2 leak.");

        // The path argument MUST still be present — Calamari needs to know where the
        // ciphertext lives. It's the password that moved out of argv, not the path.
        psi.ArgumentList.Any(a => a == "--sensitive=/tmp/some-work/sensitiveVariables.json").ShouldBeTrue(
            customMessage: "--sensitive=<path> is not secret — must still be passed via argv");
    }

    [Fact]
    public void BuildCalamariProcessStartInfo_NoSensitivePassword_NoEnvVarSet()
    {
        // Workflow without sensitive variables — no password, no env var, no --sensitive.
        var psi = LocalScriptService.BuildCalamariProcessStartInfo(
            workDir: "/tmp/some-work",
            variablesPath: "/tmp/some-work/variables.json",
            sensitiveVariablesPath: "/tmp/some-work/sensitiveVariables.json",
            sensitivePassword: null,
            sensitiveCiphertextExists: false,
            arguments: Array.Empty<string>());

        psi.Environment.ContainsKey(LocalScriptService.CalamariSensitivePasswordEnvVar).ShouldBeFalse(
            customMessage: "no sensitive vars on this deploy — env var must not be set");

        psi.ArgumentList.Any(a => a.StartsWith("--sensitive=")).ShouldBeFalse(
            customMessage: "no ciphertext — don't point Calamari at a nonexistent file");
    }

    [Fact]
    public void BuildCalamariProcessStartInfo_CiphertextMissing_NoEnvVarOrSensitiveArg()
    {
        // Defensive: if the ciphertext file somehow doesn't exist (disk issue, caller
        // bug), don't pass the password env var — Calamari would receive a password
        // with no file to decrypt. Better to surface the missing-file error loudly.
        var psi = LocalScriptService.BuildCalamariProcessStartInfo(
            workDir: "/tmp/some-work",
            variablesPath: "/tmp/some-work/variables.json",
            sensitiveVariablesPath: "/tmp/some-work/sensitiveVariables.json",
            sensitivePassword: "pw-without-ciphertext",
            sensitiveCiphertextExists: false,
            arguments: Array.Empty<string>());

        psi.Environment.ContainsKey(LocalScriptService.CalamariSensitivePasswordEnvVar).ShouldBeFalse();
        psi.ArgumentList.Any(a => a.StartsWith("--sensitive=")).ShouldBeFalse();
    }

    [Fact]
    public void BuildCalamariProcessStartInfo_UserArguments_StillForwarded()
    {
        // User-supplied script arguments must be preserved after the `--` separator.
        var psi = LocalScriptService.BuildCalamariProcessStartInfo(
            workDir: "/tmp/some-work",
            variablesPath: "/tmp/some-work/variables.json",
            sensitiveVariablesPath: "/tmp/some-work/sensitiveVariables.json",
            sensitivePassword: null,
            sensitiveCiphertextExists: false,
            arguments: new[] { "arg-one", "arg-two" });

        psi.ArgumentList.ShouldContain("--");
        psi.ArgumentList.ShouldContain("arg-one");
        psi.ArgumentList.ShouldContain("arg-two");
    }
}
