using Squid.Core.Services.DeploymentExecution.Packages;

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
            "PURGE_LIST=\"$PARENT_DIR/.squid-purge-list-$$-$RANDOM\"\n" +
            "mkdir -p \"$PARENT_DIR\"\n" +
            "mkdir -p \"$STAGING_DIR\"\n\n" +
            "is_same_version_installed() {\n" +
            "  marker=\"$FINAL_DIR/$MARKER_NAME\"\n" +
            "  [ -f \"$marker\" ] || return 1\n" +
            "  grep -F \"\\\"packageId\\\":\\\"$PACKAGE_ID\\\"\" \"$marker\" >/dev/null 2>&1 || return 1\n" +
            "  grep -F \"\\\"version\\\":\\\"$PACKAGE_VERSION\\\"\" \"$marker\" >/dev/null 2>&1 || return 1\n" +
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
            "  rel=\"$1\"\n" +
            "  [ -z \"$PRESERVE_PATHS\" ] && return 1\n" +
            "  old_ifs=$IFS\n" +
            "  IFS=\"$(printf '\\n\\t')\"\n" +
            "  for pattern in $PRESERVE_PATHS; do\n" +
            "    [ -z \"$pattern\" ] && continue\n" +
            "    case \"$pattern\" in\n" +
            "      *'**'*)\n" +
            "        prefix=${pattern%%\\*\\*}\n" +
            "        prefix=${prefix%/}\n" +
            "        case \"$rel\" in\n" +
            "          \"$prefix\"|\"$prefix\"/*) IFS=$old_ifs; return 0 ;;\n" +
            "        esac\n" +
            "        ;;\n" +
            "      *)\n" +
            "        case \"$rel\" in\n" +
            "          $pattern) IFS=$old_ifs; return 0 ;;\n" +
            "        esac\n" +
            "        ;;\n" +
            "    esac\n" +
            "  done\n" +
            "  IFS=$old_ifs\n" +
            "  return 1\n" +
            "}\n\n" +
            "purge_non_package_files() {\n" +
            "  [ \"$PURGE_BEFORE_INSTALL\" = \"True\" ] || return 0\n" +
            "  find \"$FINAL_DIR\" -type f 2>/dev/null > \"$PURGE_LIST\" || true\n" +
            "  while IFS= read -r file; do\n" +
            "    [ -z \"$file\" ] && continue\n" +
            "    rel=\"${file#\"$FINAL_DIR\"/}\"\n" +
            "    [ \"$rel\" = \"$MARKER_NAME\" ] && continue\n" +
            "    if grep -Fx -- \"$rel\" \"$PACKAGE_FILE_LIST\" >/dev/null 2>&1; then\n" +
            "      continue\n" +
            "    fi\n" +
            "    if is_preserved \"$rel\"; then\n" +
            "      continue\n" +
            "    fi\n" +
            "    rm -f -- \"$file\" 2>/dev/null || true\n" +
            "  done < \"$PURGE_LIST\"\n" +
            "  rm -f \"$PURGE_LIST\" 2>/dev/null || true\n" +
            "  find \"$FINAL_DIR\" -depth -type d -empty ! -path \"$FINAL_DIR\" -exec rmdir {} + 2>/dev/null || true\n" +
            "}\n\n" +
            "update_current_pointer() {\n" +
            "  [ \"$MODE\" = \"Versioned\" ] || return 0\n" +
            "  [ \"$USE_CURRENT_POINTER\" = \"True\" ] || return 0\n" +
            "  current_path=\"$PACKAGE_ROOT/$CURRENT_NAME\"\n" +
            "  target_name=$(basename \"$FINAL_DIR\")\n" +
            "  rm -rf -- \"$current_path\" 2>/dev/null || true\n" +
            "  if ln -s \"$target_name\" \"$current_path\" 2>/dev/null; then\n" +
            "    return 0\n" +
            "  fi\n" +
            "  printf '%s\\n' \"$target_name\" > \"$current_path\"\n" +
            "}\n\n" +
            "apply_retention() {\n" +
            "  [ \"$MODE\" = \"Versioned\" ] || return 0\n" +
            "  keep=\"$RETENTION_COUNT\"\n" +
            "  case \"$keep\" in\n" +
            "    ''|*[!0-9]*) return 0 ;;\n" +
            "  esac\n" +
            "  [ \"$keep\" -gt 0 ] || return 0\n" +
            "  current_name=$(basename \"$FINAL_DIR\")\n" +
            "  RETENTION_LIST=\"$PARENT_DIR/.squid-retention-list-$$-$RANDOM\"\n" +
            "  : > \"$RETENTION_LIST\"\n" +
            "  # Newest-first, newline-safe names (supports spaces).\n" +
            "  ls -1t \"$PACKAGE_ROOT\" 2>/dev/null | while IFS= read -r name; do\n" +
            "    [ -z \"$name\" ] && continue\n" +
            "    [ \"$name\" = \"$CURRENT_NAME\" ] && continue\n" +
            "    case \"$name\" in\n" +
            "      .squid-*) continue ;;\n" +
            "    esac\n" +
            "    [ -d \"$PACKAGE_ROOT/$name\" ] || continue\n" +
            "    printf '%s\\n' \"$name\" >> \"$RETENTION_LIST\"\n" +
            "  done\n" +
            "  count=0\n" +
            "  while IFS= read -r name; do\n" +
            "    [ -z \"$name\" ] && continue\n" +
            "    if [ \"$name\" = \"$current_name\" ]; then\n" +
            "      count=$((count + 1))\n" +
            "      continue\n" +
            "    fi\n" +
            "    if [ \"$count\" -lt \"$keep\" ]; then\n" +
            "      count=$((count + 1))\n" +
            "      continue\n" +
            "    fi\n" +
            "    rm -rf -- \"$PACKAGE_ROOT/$name\" 2>/dev/null || true\n" +
            "  done < \"$RETENTION_LIST\"\n" +
            "  rm -f \"$RETENTION_LIST\" 2>/dev/null || true\n" +
            "}\n\n" +
            "if [ \"$SKIP_IF_INSTALLED\" = \"True\" ] && is_same_version_installed; then\n" +
            "  echo \"SkipIfAlreadyInstalled: package '$PACKAGE_ID' version '$PACKAGE_VERSION' already installed at '$FINAL_DIR'.\"\n" +
            "  emit_output_vars\n" +
            "  exit 0\n" +
            "fi\n\n" +
            "cleanup() {\n" +
            "  rm -rf \"$STAGING_DIR\" 2>/dev/null || true\n" +
            "  rm -f \"$PACKAGE_FILE_LIST\" \"$PURGE_LIST\" 2>/dev/null || true\n" +
            "}\n" +
            "trap cleanup EXIT\n\n" +
            "# Extract into a clean staging directory so package-file inventory excludes pre-existing target files.\n" +
            extractCommand + "\n\n" +
            "# Capture package-relative paths from the extracted archive only.\n" +
            "find \"$STAGING_DIR\" -type f 2>/dev/null | sed \"s|^$STAGING_DIR/||\" > \"$PACKAGE_FILE_LIST\" || : > \"$PACKAGE_FILE_LIST\"\n\n" +
            "if [ \"$MODE\" = \"Custom\" ] && [ -d \"$FINAL_DIR\" ]; then\n" +
            "  # Overlay previous custom install content under extracted package files.\n" +
            "  # Package files already in staging take precedence.\n" +
            "  find \"$FINAL_DIR\" -type f 2>/dev/null | while IFS= read -r old; do\n" +
            "    [ -z \"$old\" ] && continue\n" +
            "    rel=\"${old#\"$FINAL_DIR\"/}\"\n" +
            "    if [ ! -e \"$STAGING_DIR/$rel\" ]; then\n" +
            "      mkdir -p \"$(dirname \"$STAGING_DIR/$rel\")\"\n" +
            "      cp -a -- \"$old\" \"$STAGING_DIR/$rel\" 2>/dev/null || true\n" +
            "    fi\n" +
            "  done\n" +
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
            "purge_non_package_files\n\n" +
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
            "# Only promote current/retention after conventions succeed so failed installs\n" +
            "# cannot leave operators pointed at a broken version directory.\n" +
            "update_current_pointer\n" +
            "apply_retention\n" +
            "rm -rf \"$BACKUP_DIR\" 2>/dev/null || true\n" +
            "trap - EXIT\n" +
            "rm -f \"$PACKAGE_FILE_LIST\" \"$PURGE_LIST\" 2>/dev/null || true\n\n" +
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

        var safeId = PackageInstallationPath.EncodeExternalIdentitySegment(model.PackageId, "Package");
        var safeVersion = PackageInstallationPath.EncodeExternalIdentitySegment(model.PackageVersion, "Version");
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
                "TAR_LIST_FILE=\"$PARENT_DIR/.squid-tar-list-$$-$RANDOM\"\n" +
                "if ! tar -tf \"$ARCHIVE\" > \"$TAR_LIST_FILE\"; then\n" +
                "  rm -f \"$TAR_LIST_FILE\" 2>/dev/null || true\n" +
                "  echo \"[extraction] Failed to list entries in $ARCHIVE\" >&2\n" +
                "  exit 1\n" +
                "fi\n" +
                "while IFS= read -r entry; do\n" +
                "  [ -z \"$entry\" ] && continue\n" +
                "  case \"$entry\" in\n" +
                "    /*|../*|*/../*|*/..|..) \n" +
                "      rm -f \"$TAR_LIST_FILE\" 2>/dev/null || true\n" +
                "      echo \"[extraction] Entry '$entry' would escape the destination directory (zip-slip). Aborted.\" >&2\n" +
                "      exit 1\n" +
                "      ;;\n" +
                "  esac\n" +
                "  case \"$entry\" in\n" +
                "    *\\\\..\\\\*|*\\\\..|..\\\\*) \n" +
                "      rm -f \"$TAR_LIST_FILE\" 2>/dev/null || true\n" +
                "      echo \"[extraction] Entry '$entry' would escape the destination directory (zip-slip). Aborted.\" >&2\n" +
                "      exit 1\n" +
                "      ;;\n" +
                "  esac\n" +
                "done < \"$TAR_LIST_FILE\"\n" +
                "rm -f \"$TAR_LIST_FILE\" 2>/dev/null || true\n" +
                "if ! tar " + flags + " \"$ARCHIVE\" -C \"$STAGING_DIR\"; then\n" +
                "  echo \"[extraction] Failed to extract $ARCHIVE into $STAGING_DIR\" >&2\n" +
                "  exit 1\n" +
                "fi";
        }

        // Info-ZIP supports `unzip -Z1`; BusyBox unzip (common in Alpine SSH images)
        // does not. BusyBox also rewrites traversal names while listing, so we:
        // 1) capture raw listing stderr (look for "removing leading")
        // 2) parse names from stdout
        // 3) reject absolute / parent-segment paths
        return
            "if ! command -v unzip >/dev/null 2>&1; then\n" +
            "  echo \"[extraction] unzip is required on the SSH target.\" >&2\n" +
            "  exit 1\n" +
            "fi\n" +
            "ZIP_LIST_FILE=\"$PARENT_DIR/.squid-zip-list-$$-$RANDOM\"\n" +
            "ZIP_LIST_ERR=\"$PARENT_DIR/.squid-zip-list-err-$$-$RANDOM\"\n" +
            "if unzip -Z1 \"$ARCHIVE\" > \"$ZIP_LIST_FILE\" 2>\"$ZIP_LIST_ERR\"; then\n" +
            "  :\n" +
            "else\n" +
            "  if ! unzip -l \"$ARCHIVE\" > \"$ZIP_LIST_FILE.raw\" 2>\"$ZIP_LIST_ERR\"; then\n" +
            "    rm -f \"$ZIP_LIST_FILE\" \"$ZIP_LIST_FILE.raw\" \"$ZIP_LIST_ERR\" 2>/dev/null || true\n" +
            "    echo \"[extraction] Failed to list entries in $ARCHIVE\" >&2\n" +
            "    exit 1\n" +
            "  fi\n" +
            "  if ! awk '\n" +
            "    NF >= 4 && $1 ~ /^[0-9]+$/ {\n" +
            "      $1=\"\"; $2=\"\"; $3=\"\";\n" +
            "      sub(/^ +/, \"\");\n" +
            "      if (length($0)) print\n" +
            "    }\n" +
            "  ' \"$ZIP_LIST_FILE.raw\" > \"$ZIP_LIST_FILE\"; then\n" +
            "    rm -f \"$ZIP_LIST_FILE\" \"$ZIP_LIST_FILE.raw\" \"$ZIP_LIST_ERR\" 2>/dev/null || true\n" +
            "    echo \"[extraction] Failed to list entries in $ARCHIVE\" >&2\n" +
            "    exit 1\n" +
            "  fi\n" +
            "  rm -f \"$ZIP_LIST_FILE.raw\" 2>/dev/null || true\n" +
            "fi\n" +
            "if grep -Eiq 'removing leading|zip-slip|would escape|absolute path' \"$ZIP_LIST_ERR\" 2>/dev/null; then\n" +
            "  rm -f \"$ZIP_LIST_FILE\" \"$ZIP_LIST_ERR\" 2>/dev/null || true\n" +
            "  echo \"[extraction] Archive entry would escape the destination directory (zip-slip). Aborted.\" >&2\n" +
            "  exit 1\n" +
            "fi\n" +
            "if [ ! -s \"$ZIP_LIST_FILE\" ]; then\n" +
            "  rm -f \"$ZIP_LIST_FILE\" \"$ZIP_LIST_ERR\" 2>/dev/null || true\n" +
            "  echo \"[extraction] Failed to list entries in $ARCHIVE\" >&2\n" +
            "  exit 1\n" +
            "fi\n" +
            "while IFS= read -r entry; do\n" +
            "  [ -z \"$entry\" ] && continue\n" +
            "  case \"$entry\" in\n" +
            "    /*|~/*|../*|*/../*|*/..|..|..\\\\*|*/..\\\\*|..\\\\*)\n" +
            "      rm -f \"$ZIP_LIST_FILE\" \"$ZIP_LIST_ERR\" 2>/dev/null || true\n" +
            "      echo \"[extraction] Entry '$entry' would escape the destination directory (zip-slip). Aborted.\" >&2\n" +
            "      exit 1\n" +
            "      ;;\n" +
            "  esac\n" +
            "  case \"$entry\" in\n" +
            "    *\\\\..\\\\*|*\\\\..|..\\\\*)\n" +
            "      rm -f \"$ZIP_LIST_FILE\" \"$ZIP_LIST_ERR\" 2>/dev/null || true\n" +
            "      echo \"[extraction] Entry '$entry' would escape the destination directory (zip-slip). Aborted.\" >&2\n" +
            "      exit 1\n" +
            "      ;;\n" +
            "  esac\n" +
            "done < \"$ZIP_LIST_FILE\"\n" +
            "rm -f \"$ZIP_LIST_FILE\" \"$ZIP_LIST_ERR\" 2>/dev/null || true\n" +
            "if ! unzip -q -o \"$ARCHIVE\" -d \"$STAGING_DIR\"; then\n" +
            "  echo \"[extraction] Failed to extract $ARCHIVE into $STAGING_DIR\" >&2\n" +
            "  exit 1\n" +
            "fi";
    }

    internal static string Q(string value)
    {
        value ??= string.Empty;
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }
}
