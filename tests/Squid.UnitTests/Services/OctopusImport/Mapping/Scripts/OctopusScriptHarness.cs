using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.VariableSubstitution;
using Squid.Message.Constants;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Mapping.Scripts;

/// <summary>
/// Drives an Octopus script action through the REAL import mapper, then through Squid's REAL two-pass
/// deploy-time substitution (action-property expansion, then <see cref="IntentVariableExpander"/>), then
/// through a REAL bash / pwsh process. Shell runs skip when the shell is absent (CI ubuntu has both).
/// </summary>
internal static class OctopusScriptHarness
{
    public const string TemplateId = "ActionTemplates-442";

    public static OctopusImportActionMappingResult Map(OctopusDeploymentActionDto action, OctopusActionTemplateDto template)
    {
        var templates = template == null ? null : new Dictionary<string, OctopusActionTemplateDto>(StringComparer.OrdinalIgnoreCase) { [template.Id] = template };

        return new OctopusScriptActionMapper().Map(action, new OctopusImportActionMappingContext(new OctopusImportIdMap(), 7, actionTemplates: templates));
    }

    public static OctopusDeploymentActionDto ScriptAction(string syntax, string body, params (string Key, string Value)[] extra)
    {
        var action = new OctopusDeploymentActionDto { Id = "Actions-1", Name = "Notify", ActionType = "Octopus.Script", Properties = new(StringComparer.OrdinalIgnoreCase) };

        action.Properties["Octopus.Action.Script.ScriptSource"] = "Inline";
        action.Properties["Octopus.Action.Script.Syntax"] = syntax;
        if (body != null) action.Properties["Octopus.Action.Script.ScriptBody"] = body;

        foreach (var (key, value) in extra) action.Properties[key] = value;

        return action;
    }

    public static OctopusDeploymentActionDto TemplateAction(string syntax, string body, params (string Key, string Value)[] parameterValues)
        => ScriptAction(syntax, body, parameterValues.Prepend(("Octopus.Action.Template.Id", TemplateId)).ToArray());

    public static OctopusActionTemplateDto Template(params (string Name, string Default, bool Sensitive)[] parameters)
        => new()
        {
            Id = TemplateId,
            Name = "Send deployment notification",
            ActionType = "Octopus.Script",
            Parameters = parameters.Select(p => new OctopusActionTemplateParameterDto
            {
                Name = p.Name,
                DefaultValue = p.Default == null ? null : JsonSerializer.SerializeToElement(p.Default),
                DisplaySettings = new() { ["Octopus.ControlType"] = p.Sensitive ? "Sensitive" : "SingleLineText" }
            }).ToList()
        };

    public static string ScriptBody(OctopusImportActionMappingResult result)
        => result.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.ScriptBody).PropertyValue;

    /// <summary>Squid substitutes a script body twice: once as an action property, once as the run-script intent.</summary>
    public static string DeployTimeExpand(string script, params (string Name, string Value)[] variables)
    {
        var dictionary = new VariableDictionary();

        foreach (var (name, value) in variables) dictionary.Set(name, value);

        var propertyPass = VariableExpander.ExpandString(script, dictionary);
        var intent = (RunScriptIntent)IntentVariableExpander.Expand(new RunScriptIntent { Name = "Notify", ScriptBody = propertyPass }, dictionary);

        return intent.ScriptBody;
    }

    public static Task<ShellRun> RunBashAsync(string script) => RunAsync("bash", script, ".sh");

    public static Task<ShellRun> RunPwshAsync(string script) => RunAsync("pwsh", script, ".ps1", "-NoProfile", "-NonInteractive", "-File");

    public static bool HasBash => !OperatingSystem.IsWindows() && IsOnPath("bash");

    public static bool HasPwsh => IsOnPath("pwsh");

    /// <summary>Independent reference implementation of the Octopus export cipher (not shared with production code).</summary>
    public static string OctopusEncrypt(string plaintext, string password)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(password, Encoding.UTF8.GetBytes("Octopuss"), 1_000, HashAlgorithmName.SHA1, 16);
        var iv = RandomNumberGenerator.GetBytes(16);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;

        using var encryptor = aes.CreateEncryptor();
        var bytes = Encoding.UTF8.GetBytes(plaintext);

        return $"{Convert.ToBase64String(encryptor.TransformFinalBlock(bytes, 0, bytes.Length))}|{Convert.ToBase64String(iv)}";
    }

    private static async Task<ShellRun> RunAsync(string shell, string script, string extension, params string[] leadingArgs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"squid-octopus-compat-{Guid.NewGuid():N}{extension}");

        try
        {
            await File.WriteAllTextAsync(path, script, new UTF8Encoding(false));

            var startInfo = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var arg in leadingArgs.Append(path)) startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.WaitForExitAsync(timeout.Token);

            return new ShellRun(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool IsOnPath(string executable)
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, executable)) || File.Exists(Path.Combine(dir, executable + ".exe")));
}

internal sealed record ShellRun(int ExitCode, string StdOut, string StdErr);
