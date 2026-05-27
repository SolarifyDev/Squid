using Squid.Calamari.Commands.Configuration;
using Squid.Calamari.Commands.Conventions;
using Squid.Calamari.Commands.Package;
using Squid.Calamari.Commands.StructuredConfig;
using Squid.Calamari.Commands.Substitution;
using Squid.Calamari.Execution;
using Squid.Calamari.Pipeline;
using Squid.Calamari.Scripting;

namespace Squid.Calamari.Commands;

/// <summary>
/// Handles the `run-script` subcommand.
/// Loads variables, optionally extracts a package, applies rewriter steps,
/// fires PreDeploy hook, prepends variable exports, executes the operator's
/// main script, fires PostDeploy hook, then cleans up.
///
/// <para><b>Pipeline order matters</b>:
/// <list type="number">
///   <item>ResolveWorkingDirectory — pin the cwd for the rest of the pipeline.</item>
///   <item>LoadVariablesFromFiles — read variables.json + sensitiveVariables.json.</item>
///   <item><b>ExtractPackage (G1.4)</b> — if <c>Squid.Action.Package.OriginalPath</c>
///         is set, extract the .nupkg/.zip into the working directory.</item>
///   <item><b>SubstituteInFiles → ConfigurationTransforms → JsonConfigVariables</b>
///         (G1.1 / G1.2 / G1.3) — token replacement → XDT → JSON leaf replacement.
///         Run on the extracted files so the application sees fully-prepared config.</item>
///   <item><b>PreDeploy convention (G1.5)</b> — runs <c>PreDeploy.sh</c> if it
///         exists in the working directory. After all rewriters; before the
///         operator's main script. Same variable preamble as the main script.</item>
///   <item>WriteBootstrappedBashScript — prepend `export VAR=` for the main script.</item>
///   <item>ExecuteScriptWithEngine — run the operator's main script.</item>
///   <item><b>PostDeploy convention (G1.5)</b> — runs <c>PostDeploy.sh</c>.
///         Smoke tests, cache warm-up, service-mesh registration, etc.</item>
///   <item>BuildRunScriptCommandResult — collect exit code + outputs.</item>
///   <item>CleanupTemporaryFiles — best-effort cleanup (always-runs).</item>
/// </list></para>
/// </summary>
public class RunScriptCommand
{
    private readonly ExecutionPipeline<RunScriptCommandContext> _pipeline;

    public RunScriptCommand()
        : this(new ScriptEngine())
    {
    }

    public RunScriptCommand(IScriptEngine scriptEngine)
    {
        _pipeline = new ExecutionPipeline<RunScriptCommandContext>(
        [
            new ResolveWorkingDirectoryStep<RunScriptCommandContext>(),
            new LoadVariablesFromFilesStep<RunScriptCommandContext>(),
            // G1.4 — package extraction. No-op when the wire literal is unset.
            // MUST run before any rewriter step so SubstituteInFiles +
            // ConfigurationTransforms + JsonConfigVariables have files to
            // operate on.
            new ExtractPackageStep(),
            // G1.1 — token replacement in operator-nominated text files.
            // Runs first so any #{Token} inside transform files is resolved
            // before XDT engine reads them.
            new SubstituteInFilesStep(),
            // G1.2 — XDT (web.{Env}.config) transforms on *.config files.
            // Runs after SubstituteInFiles so transform sources have
            // concrete values, not unresolved tokens.
            new ConfigurationTransformsStep(),
            // G1.3 — JSON-path leaf replacement on operator-nominated JSON
            // files (typically appsettings.json). Runs after XDT so the
            // ConfigurationTransforms-rewritten *.config files don't get
            // their JSON cousins out of sync. Matches Octopus pipeline order:
            // SubstituteInFiles → ConfigurationTransforms → JsonConfigVariables.
            new StructuredConfigVariablesStep(),
            // G1.5 — PreDeploy convention hook. Runs only when the package
            // ships a `PreDeploy.sh` file. Operator's chance to do setup
            // that needs the rewritten configs but happens before the main
            // script starts the application (e.g. database migrations,
            // permission fixes, cache invalidation).
            new ConventionScriptStep(ConventionScriptNames.PreDeploy, scriptEngine),
            new WriteBootstrappedScriptStep(),
            new ExecuteScriptWithEngineStep(scriptEngine),
            // G1.5 — PostDeploy convention hook. Runs only when the package
            // ships a `PostDeploy.sh` file. Smoke tests, cache warm-up,
            // registration with a service mesh, etc.
            new ConventionScriptStep(ConventionScriptNames.PostDeploy, scriptEngine),
            new BuildRunScriptCommandResultStep(),
            // DeployFailed convention — IAlwaysRun (cleanup phase). Fires only
            // when an upstream step threw OR the main script returned non-zero.
            // Listed BEFORE the temp-file cleanup so the failure script can
            // still see the bootstrapped + extracted files for forensic capture.
            new DeployFailedConventionStep(scriptEngine),
            new CleanupTemporaryFilesStep<RunScriptCommandContext>()
        ]);
    }

    public async Task<int> ExecuteAsync(
        string scriptPath,
        string variablesPath,
        string? sensitivePath,
        string? password,
        CancellationToken ct)
        => (await ExecuteWithResultAsync(scriptPath, variablesPath, sensitivePath, password, ct)
            .ConfigureAwait(false)).ExitCode;

    public async Task<CommandExecutionResult> ExecuteWithResultAsync(
        string scriptPath,
        string variablesPath,
        string? sensitivePath,
        string? password,
        CancellationToken ct)
    {
        var context = new RunScriptCommandContext
        {
            ScriptPath = scriptPath,
            VariablesPath = variablesPath,
            SensitivePath = sensitivePath,
            Password = password
        };

        await _pipeline.ExecuteAsync(context, ct).ConfigureAwait(false);

        return context.CommandResult
               ?? throw new InvalidOperationException("Pipeline completed without producing a command result.");
    }
}
