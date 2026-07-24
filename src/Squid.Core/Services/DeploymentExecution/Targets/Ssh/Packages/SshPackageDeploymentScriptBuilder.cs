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
    /// <summary>Remote archive file name under the package base directory (e.g. Acme.Web.1.0.0.tar.gz).</summary>
    public string ArchiveFileName { get; init; } = string.Empty;
    /// <summary>Remote package cache directory. Empty means default $HOME/.squid/Packages.</summary>
    public string PackageBaseDirectory { get; init; } = string.Empty;

    public bool SkipIfAlreadyInstalled { get; init; }
    public bool PurgeBeforeInstall { get; init; }
    public string PreservePaths { get; init; } = string.Empty;
    public int RetentionCount { get; init; }
    public bool UseCurrentPointer { get; init; }
    public bool RollbackOnFailure { get; init; } = true;
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
        var archiveFileName = ResolveArchiveFileName(model);
        var archive = Q(archiveFileName);
        var packageBaseDir = Q(model.PackageBaseDirectory ?? string.Empty);
        var preservePaths = Q(model.PreservePaths ?? string.Empty);
        var skip = model.SkipIfAlreadyInstalled ? "True" : "False";
        var purge = model.PurgeBeforeInstall ? "True" : "False";
        var useCurrent = model.UseCurrentPointer ? "True" : "False";
        var rollback = model.RollbackOnFailure ? "True" : "False";
        var retention = Math.Max(0, model.RetentionCount).ToString();
        var extractCommand = BuildExtractCommand(archiveFileName);

        return
            "#!/usr/bin/env bash\n" +
            "set -euo pipefail\n\n" +
            "if ! command -v sha256sum >/dev/null 2>&1; then\n" +
            "  echo \"[hash verification] sha256sum is required on the SSH target.\" >&2\n" +
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
            $"ARCHIVE_NAME={archive}\n" +
            $"PACKAGE_BASE_DIR={packageBaseDir}\n" +
            $"SKIP_IF_INSTALLED={Q(skip)}\n" +
            $"PURGE_BEFORE_INSTALL={Q(purge)}\n" +
            $"PRESERVE_PATHS={preservePaths}\n" +
            $"RETENTION_COUNT={Q(retention)}\n" +
            $"USE_CURRENT_POINTER={Q(useCurrent)}\n" +
            $"ROLLBACK_ON_FAILURE={Q(rollback)}\n" +
            "MARKER_NAME='.squid-installed.json'\n" +
            "CURRENT_NAME='current'\n" +
            "if [ -n \"$PACKAGE_BASE_DIR\" ]; then\n" +
            "  ARCHIVE=\"$PACKAGE_BASE_DIR/$ARCHIVE_NAME\"\n" +
            "else\n" +
            "  ARCHIVE=\"$HOME/.squid/Packages/$ARCHIVE_NAME\"\n" +
            "fi\n\n" +
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
            "PACKAGE_ROOT=\"$PARENT_DIR\"\n" +
            "STAGING_DIR=\"$PARENT_DIR/.squid-staging-$$-$RANDOM\"\n" +
            "BACKUP_DIR=\"$PARENT_DIR/.squid-backup-$$-$RANDOM\"\n" +
            "PACKAGE_FILE_LIST=\"$PARENT_DIR/.squid-package-files-$$-$RANDOM\"\n" +
            "mkdir -p \"$PARENT_DIR\"\n" +
            "mkdir -p \"$STAGING_DIR\"\n\n" +
            "is_same_version_installed() {\n" +
            "  local marker=\"$FINAL_DIR/$MARKER_NAME\"\n" +
            "  [ -f \"$marker\" ] || return 1\n" +
            "  grep -q \"\\\"packageId\\\":\\\"$PACKAGE_ID\\\"\" \"$marker\" 2>/dev/null || return 1\n" +
            "  grep -q \"\\\"version\\\":\\\"$PACKAGE_VERSION\\\"\" \"$marker\" 2>/dev/null || return 1\n" +
            "  return 0\n" +
            "}\n\n" +
            "emit_output_vars() {\n" +
            "  printf \"##squid[setVariable name='%s' value='%s']\\n\" \"Squid.Action.Package.InstallationDirectoryPath\" \"$FINAL_DIR\"\n" +
            "  printf \"##squid[setVariable name='%s' value='%s']\\n\" \"Squid.Action.Package.PackageId\" \"$PACKAGE_ID\"\n" +
            "  printf \"##squid[setVariable name='%s' value='%s']\\n\" \"Squid.Action.Package.PackageVersion\" \"$PACKAGE_VERSION\"\n" +
            "}\n\n" +
            "write_installed_marker() {\n" +
            "  printf '{\"packageId\":\"%s\",\"version\":\"%s\",\"installedAtUtc\":\"%s\"}\\n' \\\n" +
            "    \"$PACKAGE_ID\" \"$PACKAGE_VERSION\" \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\" > \"$FINAL_DIR/$MARKER_NAME\"\n" +
            "}\n\n" +
            "is_preserved() {\n" +
            "  local rel=\"$1\"\n" +
            "  [ -z \"$PRESERVE_PATHS\" ] && return 1\n" +
            "  while IFS= read -r pattern; do\n" +
            "    [ -z \"$pattern\" ] && continue\n" +
            "    case \"$rel\" in\n" +
            "      $pattern) return 0 ;;\n" +
            "    esac\n" +
            "  done <<EOF\n" +
            "$PRESERVE_PATHS\n" +
            "EOF\n" +
            "  return 1\n" +
            "}\n\n" +
            "purge_non_package_files() {\n" +
            "  [ \"$PURGE_BEFORE_INSTALL\" = \"True\" ] || return 0\n" +
            "  while IFS= read -r -d '' file; do\n" +
            "    local rel=\"${file#\"$FINAL_DIR\"/}\"\n" +
            "    [ \"$rel\" = \"$MARKER_NAME\" ] && continue\n" +
            "    if grep -Fxq -- \"$rel\" \"$PACKAGE_FILE_LIST\" 2>/dev/null; then\n" +
            "      continue\n" +
            "    fi\n" +
            "    if is_preserved \"$rel\"; then\n" +
            "      continue\n" +
            "    fi\n" +
            "    rm -f -- \"$file\" 2>/dev/null || true\n" +
            "  done < <(find \"$FINAL_DIR\" -type f -print0 2>/dev/null)\n" +
            "  find \"$FINAL_DIR\" -depth -type d -empty ! -path \"$FINAL_DIR\" -delete 2>/dev/null || true\n" +
            "}\n\n" +
            "update_current_pointer() {\n" +
            "  [ \"$MODE\" = \"Versioned\" ] || return 0\n" +
            "  [ \"$USE_CURRENT_POINTER\" = \"True\" ] || return 0\n" +
            "  local current_path=\"$PACKAGE_ROOT/$CURRENT_NAME\"\n" +
            "  local target_name\n" +
            "  target_name=$(basename \"$FINAL_DIR\")\n" +
            "  rm -rf -- \"$current_path\" 2>/dev/null || true\n" +
            "  if ln -s \"$target_name\" \"$current_path\" 2>/dev/null; then\n" +
            "    return 0\n" +
            "  fi\n" +
            "  printf '%s\\n' \"$target_name\" > \"$current_path\"\n" +
            "}\n\n" +
            "apply_retention() {\n" +
            "  [ \"$MODE\" = \"Versioned\" ] || return 0\n" +
            "  local keep=\"$RETENTION_COUNT\"\n" +
            "  case \"$keep\" in\n" +
            "    ''|*[!0-9]*) return 0 ;;\n" +
            "  esac\n" +
            "  [ \"$keep\" -gt 0 ] || return 0\n" +
            "  local current_full\n" +
            "  current_full=$(cd \"$FINAL_DIR\" && pwd -P)\n" +
            "  # Keep newest directories by mtime, always include current install.\n" +
            "  mapfile -t version_dirs < <(find \"$PACKAGE_ROOT\" -mindepth 1 -maxdepth 1 -type d ! -name '.*' ! -name \"$CURRENT_NAME\" -printf '%T@ %p\\n' 2>/dev/null | sort -nr | awk '{print $2}')\n" +
            "  declare -A keep_set=()\n" +
            "  keep_set[\"$current_full\"]=1\n" +
            "  local count=1\n" +
            "  local dir full\n" +
            "  for dir in \"${version_dirs[@]:-}\"; do\n" +
            "    [ -z \"$dir\" ] && continue\n" +
            "    full=$(cd \"$dir\" && pwd -P)\n" +
            "    if [ -n \"${keep_set[$full]:-}\" ]; then\n" +
            "      continue\n" +
            "    fi\n" +
            "    if [ \"$count\" -ge \"$keep\" ]; then\n" +
            "      break\n" +
            "    fi\n" +
            "    keep_set[\"$full\"]=1\n" +
            "    count=$((count + 1))\n" +
            "  done\n" +
            "  for dir in \"${version_dirs[@]:-}\"; do\n" +
            "    [ -z \"$dir\" ] && continue\n" +
            "    full=$(cd \"$dir\" && pwd -P)\n" +
            "    if [ -z \"${keep_set[$full]:-}\" ]; then\n" +
            "      rm -rf -- \"$dir\" 2>/dev/null || true\n" +
            "    fi\n" +
            "  done\n" +
            "}\n\n" +
            "if [ \"$SKIP_IF_INSTALLED\" = \"True\" ] && is_same_version_installed; then\n" +
            "  echo \"SkipIfAlreadyInstalled: package '$PACKAGE_ID' version '$PACKAGE_VERSION' already installed at '$FINAL_DIR'.\"\n" +
            "  emit_output_vars\n" +
            "  exit 0\n" +
            "fi\n\n" +
            "cleanup() {\n" +
            "  rm -rf \"$STAGING_DIR\" 2>/dev/null || true\n" +
            "  rm -f \"$PACKAGE_FILE_LIST\" 2>/dev/null || true\n" +
            "}\n" +
            "trap cleanup EXIT\n\n" +
            "if [ \"$MODE\" = \"Custom\" ] && [ -d \"$FINAL_DIR\" ]; then\n" +
            "  cp -a \"$FINAL_DIR\"/. \"$STAGING_DIR\"/\n" +
            "fi\n\n" +
            extractCommand + "\n\n" +
            "# Capture package-relative paths before commit for purge support.\n" +
            ": > \"$PACKAGE_FILE_LIST\"\n" +
            "while IFS= read -r -d '' file; do\n" +
            "  printf '%s\\n' \"${file#\"$STAGING_DIR\"/}\" >> \"$PACKAGE_FILE_LIST\"\n" +
            "done < <(find \"$STAGING_DIR\" -type f -print0 2>/dev/null)\n\n" +
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
            "purge_non_package_files\n" +
            "update_current_pointer\n\n" +
            "cd \"$FINAL_DIR\"\n" +
            "if [ -f \"PreDeploy.sh\" ]; then\n" +
            "  echo \"PreDeploy: running PreDeploy.sh\"\n" +
            "  if ! bash \"PreDeploy.sh\"; then\n" +
            "    echo \"[rollback] PreDeploy failed; restoring previous installation if available.\" >&2\n" +
            "    if [ \"$ROLLBACK_ON_FAILURE\" = \"True\" ] && [ -d \"$BACKUP_DIR\" ]; then\n" +
            "      rm -rf \"$FINAL_DIR\"\n" +
            "      if ! mv \"$BACKUP_DIR\" \"$FINAL_DIR\"; then\n" +
            "        echo \"[rollback] Failed to restore backup after PreDeploy failure. Backup path: $BACKUP_DIR\" >&2\n" +
            "        exit 1\n" +
            "      fi\n" +
            "    fi\n" +
            "    exit 1\n" +
            "  fi\n" +
            "fi\n" +
            "if [ -f \"PostDeploy.sh\" ]; then\n" +
            "  echo \"PostDeploy: running PostDeploy.sh\"\n" +
            "  if ! bash \"PostDeploy.sh\"; then\n" +
            "    echo \"[rollback] PostDeploy failed; keeping installed content and discarding backup.\" >&2\n" +
            "    rm -rf \"$BACKUP_DIR\" 2>/dev/null || true\n" +
            "    exit 1\n" +
            "  fi\n" +
            "fi\n\n" +
            "write_installed_marker\n" +
            "apply_retention\n" +
            "rm -rf \"$BACKUP_DIR\" 2>/dev/null || true\n" +
            "trap - EXIT\n" +
            "rm -f \"$PACKAGE_FILE_LIST\" 2>/dev/null || true\n\n" +
            "emit_output_vars\n" +
            "echo \"DeployPackage: installed to $FINAL_DIR\"\n";
    }

    internal static string ResolveArchiveFileName(SshPackageDeployScriptModel model)
    {
        if (!string.IsNullOrWhiteSpace(model.ArchiveFileName))
        {
            var name = model.ArchiveFileName.Trim().Replace('\\', '/');
            var slash = name.LastIndexOf('/');
            return slash >= 0 ? name[(slash + 1)..] : name;
        }

        var safeId = SanitizeSegment(model.PackageId);
        var safeVersion = SanitizeSegment(model.PackageVersion);
        return $"{safeId}.{safeVersion}.nupkg";
    }

    internal static string BuildExtractCommand(string archiveFileName)
    {
        var lower = (archiveFileName ?? string.Empty).ToLowerInvariant();
        if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz") || lower.EndsWith(".tar"))
        {
            var flags = lower.EndsWith(".tar") ? "-xf" : "-xzf";
            return
                "if ! command -v tar >/dev/null 2>&1; then\n" +
                "  echo \"[extraction] tar is required on the SSH target.\" >&2\n" +
                "  exit 1\n" +
                "fi\n" +
                "if ! tar " + flags + " \"$ARCHIVE\" -C \"$STAGING_DIR\"; then\n" +
                "  echo \"[extraction] Failed to extract $ARCHIVE into $STAGING_DIR\" >&2\n" +
                "  exit 1\n" +
                "fi";
        }

        return
            "if ! command -v unzip >/dev/null 2>&1; then\n" +
            "  echo \"[extraction] unzip is required on the SSH target.\" >&2\n" +
            "  exit 1\n" +
            "fi\n" +
            "if ! unzip -q -o \"$ARCHIVE\" -d \"$STAGING_DIR\"; then\n" +
            "  echo \"[extraction] Failed to extract $ARCHIVE into $STAGING_DIR\" >&2\n" +
            "  exit 1\n" +
            "fi";
    }

    internal static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "package";

        var chars = value.Select(c => c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrEmpty(sanitized) ? "package" : sanitized;
    }

    internal static string Q(string value)
    {
        value ??= string.Empty;
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }
}
