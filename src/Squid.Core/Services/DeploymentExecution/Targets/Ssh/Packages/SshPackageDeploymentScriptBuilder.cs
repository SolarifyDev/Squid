namespace Squid.Core.Services.DeploymentExecution.Ssh.Packages;

public sealed class SshPackageDeployScriptModel
{
    public required string ExpectedSha256 { get; init; }
    public required string Mode { get; init; }
    public string EnvironmentSegment { get; init; } = string.Empty;
    public string ProjectSegment { get; init; } = string.Empty;
    public string PackageSegment { get; init; } = string.Empty;
    public string VersionSegment { get; init; } = string.Empty;
    public string CustomInstallationDirectory { get; init; } = string.Empty;
    public required string PackageId { get; init; }
    public required string PackageVersion { get; init; }
}

public static class SshPackageDeploymentScriptBuilder
{
    public static string Build(SshPackageDeployScriptModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var hash = Q(model.ExpectedSha256);
        var packageId = Q(model.PackageId);
        var packageVersion = Q(model.PackageVersion);
        var mode = Q(model.Mode ?? "Versioned");
        var env = Q(model.EnvironmentSegment);
        var project = Q(model.ProjectSegment);
        var package = Q(model.PackageSegment);
        var version = Q(model.VersionSegment);
        var custom = Q(model.CustomInstallationDirectory ?? string.Empty);

        return
            "#!/usr/bin/env bash\n" +
            "set -euo pipefail\n\n" +
            "if ! command -v sha256sum >/dev/null 2>&1; then\n" +
            "  echo \"[hash verification] sha256sum is required on the SSH target.\" >&2\n" +
            "  exit 1\n" +
            "fi\n" +
            "if ! command -v unzip >/dev/null 2>&1; then\n" +
            "  echo \"[extraction] unzip is required on the SSH target.\" >&2\n" +
            "  exit 1\n" +
            "fi\n" +
            "if [ -z \"${HOME:-}\" ]; then\n" +
            "  echo \"[target path validation] $HOME could not be resolved on the SSH target.\" >&2\n" +
            "  exit 1\n" +
            "fi\n\n" +
            $"EXPECTED_HASH={hash}\n" +
            $"MODE={mode}\n" +
            $"PACKAGE_ID={packageId}\n" +
            $"PACKAGE_VERSION={packageVersion}\n" +
            $"ENV_SEG={env}\n" +
            $"PROJECT_SEG={project}\n" +
            $"PACKAGE_SEG={package}\n" +
            $"VERSION_SEG={version}\n" +
            $"CUSTOM_DIR={custom}\n" +
            "ARCHIVE=\"$HOME/.squid/Packages/${PACKAGE_ID}.${PACKAGE_VERSION}.nupkg\"\n\n" +
            "if [ ! -f \"$ARCHIVE\" ]; then\n" +
            "  echo \"[transfer] Package archive not found: $ARCHIVE\" >&2\n" +
            "  exit 1\n" +
            "fi\n\n" +
            "ACTUAL_HASH=$(sha256sum \"$ARCHIVE\" | awk '{print $1}')\n" +
            "if [ \"$ACTUAL_HASH\" != \"$EXPECTED_HASH\" ]; then\n" +
            "  echo \"[hash verification] SHA-256 mismatch: expected $EXPECTED_HASH, got $ACTUAL_HASH\" >&2\n" +
            "  exit 1\n" +
            "fi\n\n" +
            "if [ \"$MODE\" = \"Custom\" ]; then\n" +
            "  FINAL_DIR=\"$CUSTOM_DIR\"\n" +
            "  if [ -z \"$FINAL_DIR\" ] || [ \"${FINAL_DIR:0:1}\" != \"/\" ] || [ \"$FINAL_DIR\" = \"/\" ]; then\n" +
            "    echo \"[target path validation] Custom installation directory must be a non-root absolute path.\" >&2\n" +
            "    exit 1\n" +
            "  fi\n" +
            "else\n" +
            "  FINAL_DIR=\"$HOME/.squid/Applications/$ENV_SEG/$PROJECT_SEG/$PACKAGE_SEG/$VERSION_SEG\"\n" +
            "fi\n\n" +
            "PARENT_DIR=$(dirname \"$FINAL_DIR\")\n" +
            "STAGING_DIR=\"$PARENT_DIR/.squid-staging-$$-$RANDOM\"\n" +
            "BACKUP_DIR=\"$PARENT_DIR/.squid-backup-$$-$RANDOM\"\n" +
            "mkdir -p \"$PARENT_DIR\"\n" +
            "mkdir -p \"$STAGING_DIR\"\n\n" +
            "cleanup() {\n" +
            "  rm -rf \"$STAGING_DIR\" 2>/dev/null || true\n" +
            "}\n" +
            "trap cleanup EXIT\n\n" +
            "if [ \"$MODE\" = \"Custom\" ] && [ -d \"$FINAL_DIR\" ]; then\n" +
            "  cp -a \"$FINAL_DIR\"/. \"$STAGING_DIR\"/\n" +
            "fi\n\n" +
            "if ! unzip -q -o \"$ARCHIVE\" -d \"$STAGING_DIR\"; then\n" +
            "  echo \"[extraction] Failed to extract $ARCHIVE into $STAGING_DIR\" >&2\n" +
            "  exit 1\n" +
            "fi\n\n" +
            "if [ -d \"$FINAL_DIR\" ]; then\n" +
            "  mv \"$FINAL_DIR\" \"$BACKUP_DIR\"\n" +
            "fi\n\n" +
            "if ! mv \"$STAGING_DIR\" \"$FINAL_DIR\"; then\n" +
            "  echo \"[final-directory commit] Failed to commit staging directory to $FINAL_DIR\" >&2\n" +
            "  if [ -d \"$BACKUP_DIR\" ]; then\n" +
            "    mv \"$BACKUP_DIR\" \"$FINAL_DIR\" || true\n" +
            "  fi\n" +
            "  exit 1\n" +
            "fi\n\n" +
            "rm -rf \"$BACKUP_DIR\" 2>/dev/null || true\n" +
            "trap - EXIT\n\n" +
            "cd \"$FINAL_DIR\"\n" +
            "if [ -f \"PreDeploy.sh\" ]; then\n" +
            "  echo \"PreDeploy: running PreDeploy.sh\"\n" +
            "  bash \"PreDeploy.sh\"\n" +
            "fi\n" +
            "if [ -f \"PostDeploy.sh\" ]; then\n" +
            "  echo \"PostDeploy: running PostDeploy.sh\"\n" +
            "  bash \"PostDeploy.sh\"\n" +
            "fi\n\n" +
            "printf \"##squid[setVariable name='%s' value='%s']\\n\" \"Squid.Action.Package.InstallationDirectoryPath\" \"$FINAL_DIR\"\n" +
            "printf \"##squid[setVariable name='%s' value='%s']\\n\" \"Squid.Action.Package.PackageId\" \"$PACKAGE_ID\"\n" +
            "printf \"##squid[setVariable name='%s' value='%s']\\n\" \"Squid.Action.Package.PackageVersion\" \"$PACKAGE_VERSION\"\n" +
            "echo \"DeployPackage: installed to $FINAL_DIR\"\n";
    }

    internal static string Q(string value)
    {
        value ??= string.Empty;
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }
}
