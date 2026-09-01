using Squid.Calamari.Scripting;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands.Package;

internal sealed class DeployPackageCommand
{
    private readonly IScriptEngine _scriptEngine;

    public DeployPackageCommand(IScriptEngine? scriptEngine = null)
    {
        _scriptEngine = scriptEngine ?? new ScriptEngine();
    }

    public async Task<int> ExecuteAsync(
        string archivePath,
        string? variablesPath,
        string? sensitivePath,
        string? password,
        string? expectedHash,
        string? mode,
        string? finalDir,
        CancellationToken ct)
    {
        var variables = string.IsNullOrWhiteSpace(variablesPath)
            ? new VariableSet()
            : VariableSetFactory.CreateFromFiles(variablesPath, sensitivePath, password);

        archivePath = FirstNonEmpty(archivePath, variables.Get("Squid.Action.Package.OriginalPath"))
            ?? throw new InvalidOperationException("[package identity validation] --archive is required.");

        if (!Path.IsPathRooted(archivePath))
            archivePath = Path.GetFullPath(archivePath);

        expectedHash ??= variables.Get("Squid.Action.Package.Hash");
        mode ??= variables.Get("Squid.Action.Package.InstallationDirectoryMode") ?? "Versioned";
        finalDir ??= variables.Get("Squid.Action.Package.InstallationDirectoryPath");

        if (string.IsNullOrWhiteSpace(finalDir))
            finalDir = ResolveFinalDirectory(mode, variables);

        var packageId = variables.Get("Squid.Action.Package.PackageId") ?? string.Empty;
        var packageVersion = variables.Get("Squid.Action.Package.PackageVersion") ?? string.Empty;
        var preferredSyntax = OperatingSystem.IsWindows() ? ScriptSyntax.PowerShell : ScriptSyntax.Bash;

        var result = await PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = archivePath,
            ExpectedSha256 = expectedHash ?? string.Empty,
            Mode = mode,
            FinalInstallationDirectory = finalDir,
            PreferredSyntax = preferredSyntax,
            Variables = variables,
            ScriptEngine = _scriptEngine,
            PackageId = packageId,
            PackageVersion = packageVersion
        }, ct).ConfigureAwait(false);

        Console.WriteLine(
            $"DeployPackage: installed to '{result.InstallationDirectory}' " +
            $"({result.FilesExtracted:N0} file(s), {result.TotalBytesWritten:N0} bytes).");
        return 0;
    }

    internal static string ResolveFinalDirectory(string mode, VariableSet variables)
    {
        if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            var custom = variables.Get("Squid.Action.Package.CustomInstallationDirectory");
            if (string.IsNullOrWhiteSpace(custom))
                throw new InvalidOperationException("[target path validation] Custom installation directory is required when mode is Custom.");
            return custom;
        }

        var env = RequireSegment(variables, "Squid.Action.Package.Path.Environment", "Environment");
        var project = RequireSegment(variables, "Squid.Action.Package.Path.Project", "Project");
        var package = RequireSegment(variables, "Squid.Action.Package.Path.Package", "Package");
        var version = RequireSegment(variables, "Squid.Action.Package.Path.Version", "Version");

        var root = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Squid", "Tentacle", "Applications")
            : "/var/lib/squid-tentacle/Applications";

        return Path.Combine(root, env, project, package, version);
    }

    private static string RequireSegment(VariableSet variables, string name, string label)
    {
        var value = variables.Get(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"[target path validation] Missing {label} path segment variable '{name}'.");
        return value.Trim();
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
