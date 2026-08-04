using System.Text.RegularExpressions;

namespace Squid.Core.Services.OctopusImport.Octopus;

public static partial class OctopusDocumentClassifier
{
    public static OctopusDocumentClassification Classify(OctopusManifestEntryDto manifestEntry)
    {
        ArgumentNullException.ThrowIfNull(manifestEntry);

        var sourcePath = manifestEntry.DocumentSource;
        var sourceId = manifestEntry.Id;
        var kind = MapManifestDocumentType(manifestEntry.DocumentType);
        var isSnapshot = IsSnapshotId(sourceId) || IsSnapshotFileName(sourcePath);

        kind = ApplySnapshotKind(kind, isSnapshot);

        return new OctopusDocumentClassification(
            kind,
            sourcePath,
            sourceId,
            manifestEntry.DocumentType,
            IsSnapshotKind(kind),
            IsOutOfScopeHistory(kind));
    }

    public static OctopusDocumentClassification ClassifyFileName(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return Unknown(sourcePath);

        var fileName = Path.GetFileName(sourcePath);

        if (string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase))
            return Build(OctopusDocumentKind.Manifest, sourcePath);

        var isSnapshot = IsSnapshotFileName(fileName);
        var kind = MapFileName(fileName);
        kind = ApplySnapshotKind(kind, isSnapshot);

        return Build(kind, sourcePath);
    }

    public static OctopusDocumentClassification ClassifyJsonDocument(string sourcePath, string id, string documentType = null)
    {
        if (!string.IsNullOrWhiteSpace(documentType))
        {
            var kind = MapManifestDocumentType(documentType);
            var isSnapshot = IsSnapshotId(id) || IsSnapshotFileName(sourcePath);
            kind = ApplySnapshotKind(kind, isSnapshot);

            return new OctopusDocumentClassification(
                kind,
                sourcePath,
                id,
                documentType,
                IsSnapshotKind(kind),
                IsOutOfScopeHistory(kind));
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            var isSnapshot = IsSnapshotId(id) || IsSnapshotFileName(sourcePath);
            var kind = MapId(id);
            kind = ApplySnapshotKind(kind, isSnapshot);

            return new OctopusDocumentClassification(
                kind,
                sourcePath,
                id,
                null,
                IsSnapshotKind(kind),
                IsOutOfScopeHistory(kind));
        }

        return ClassifyFileName(sourcePath);
    }

    private static OctopusDocumentClassification Build(OctopusDocumentKind kind, string sourcePath)
        => new(kind, sourcePath, null, null, IsSnapshotKind(kind), IsOutOfScopeHistory(kind));

    private static OctopusDocumentClassification Unknown(string sourcePath)
        => new(OctopusDocumentKind.Unknown, sourcePath, null, null, false, false);

    private static OctopusDocumentKind MapManifestDocumentType(string documentType)
    {
        if (string.IsNullOrWhiteSpace(documentType))
            return OctopusDocumentKind.Unknown;

        return documentType.Trim() switch
        {
            "Project" => OctopusDocumentKind.Project,
            "ProjectGroup" => OctopusDocumentKind.ProjectGroup,
            "StaticDeploymentEnvironment" => OctopusDocumentKind.Environment,
            "Environment" => OctopusDocumentKind.Environment,
            "Lifecycle" => OctopusDocumentKind.Lifecycle,
            "Channel" => OctopusDocumentKind.Channel,
            "DeploymentSettings" => OctopusDocumentKind.DeploymentSettings,
            "DeploymentProcess" => OctopusDocumentKind.DeploymentProcess,
            "ProjectVariables" => OctopusDocumentKind.VariableSet,
            "VariableSet" => OctopusDocumentKind.VariableSet,
            "DockerFeed" => OctopusDocumentKind.Feed,
            "NuGetFeed" => OctopusDocumentKind.Feed,
            "HelmFeed" => OctopusDocumentKind.Feed,
            "GitHubFeed" => OctopusDocumentKind.Feed,
            "Feed" => OctopusDocumentKind.Feed,
            "ConfigurableTeam" => OctopusDocumentKind.Team,
            "Team" => OctopusDocumentKind.Team,
            "Machine" => OctopusDocumentKind.Machine,
            "Account" => OctopusDocumentKind.Account,
            "Certificate" => OctopusDocumentKind.Certificate,
            "Release" => OctopusDocumentKind.Release,
            "Deployment" => OctopusDocumentKind.Deployment,
            "ServerTask" => OctopusDocumentKind.ServerTask,
            "ActionTemplate" => OctopusDocumentKind.ActionTemplate,
            _ => OctopusDocumentKind.Unknown
        };
    }

    private static OctopusDocumentKind MapFileName(string fileName)
    {
        var lower = fileName.ToLowerInvariant();

        if (lower.StartsWith("deploymentprocess-", StringComparison.Ordinal))
            return OctopusDocumentKind.DeploymentProcess;

        if (lower.StartsWith("variableset-", StringComparison.Ordinal))
            return OctopusDocumentKind.VariableSet;

        return MapId(fileName);
    }

    private static OctopusDocumentKind MapId(string idOrFileName)
    {
        if (string.IsNullOrWhiteSpace(idOrFileName))
            return OctopusDocumentKind.Unknown;

        var value = idOrFileName.Trim();

        if (value.StartsWith("Projects-", StringComparison.OrdinalIgnoreCase))
            return OctopusDocumentKind.Project;
        if (value.StartsWith("ProjectGroups-", StringComparison.OrdinalIgnoreCase))
            return OctopusDocumentKind.ProjectGroup;
        if (value.StartsWith("Environments-", StringComparison.OrdinalIgnoreCase))
            return OctopusDocumentKind.Environment;
        if (value.StartsWith("Lifecycles-", StringComparison.OrdinalIgnoreCase))
            return OctopusDocumentKind.Lifecycle;
        if (value.StartsWith("Channels-", StringComparison.OrdinalIgnoreCase))
            return OctopusDocumentKind.Channel;
        if (value.StartsWith("deploymentsettings-", StringComparison.OrdinalIgnoreCase))
            return OctopusDocumentKind.DeploymentSettings;
        if (value.StartsWith("Feeds-", StringComparison.OrdinalIgnoreCase))
            return OctopusDocumentKind.Feed;
        if (value.StartsWith("Teams-", StringComparison.OrdinalIgnoreCase))
            return OctopusDocumentKind.Team;
        if (value.StartsWith("Machines-", StringComparison.OrdinalIgnoreCase))
            return OctopusDocumentKind.Machine;
        if (value.StartsWith("Accounts-", StringComparison.OrdinalIgnoreCase))
            return OctopusDocumentKind.Account;
        if (value.StartsWith("Certificates-", StringComparison.OrdinalIgnoreCase))
            return OctopusDocumentKind.Certificate;
        if (value.StartsWith("Releases-", StringComparison.OrdinalIgnoreCase))
            return OctopusDocumentKind.Release;
        if (value.StartsWith("Deployments-", StringComparison.OrdinalIgnoreCase))
            return OctopusDocumentKind.Deployment;
        if (value.StartsWith("ServerTasks-", StringComparison.OrdinalIgnoreCase))
            return OctopusDocumentKind.ServerTask;
        if (value.StartsWith("ActionTemplates-", StringComparison.OrdinalIgnoreCase))
            return OctopusDocumentKind.ActionTemplate;

        return OctopusDocumentKind.Unknown;
    }

    private static OctopusDocumentKind ApplySnapshotKind(OctopusDocumentKind kind, bool isSnapshot)
    {
        if (!isSnapshot)
            return kind;

        return kind switch
        {
            OctopusDocumentKind.DeploymentProcess => OctopusDocumentKind.DeploymentProcessSnapshot,
            OctopusDocumentKind.VariableSet => OctopusDocumentKind.VariableSetSnapshot,
            _ => kind
        };
    }

    private static bool IsSnapshotKind(OctopusDocumentKind kind)
        => kind is OctopusDocumentKind.DeploymentProcessSnapshot or OctopusDocumentKind.VariableSetSnapshot;

    private static bool IsOutOfScopeHistory(OctopusDocumentKind kind)
        => IsSnapshotKind(kind) || kind is OctopusDocumentKind.Release or OctopusDocumentKind.Deployment or OctopusDocumentKind.ServerTask;

    private static bool IsSnapshotId(string id)
        => !string.IsNullOrWhiteSpace(id) && SnapshotIdPattern().IsMatch(id);

    private static bool IsSnapshotFileName(string sourcePath)
        => !string.IsNullOrWhiteSpace(sourcePath) && SnapshotFileNamePattern().IsMatch(Path.GetFileName(sourcePath));

    [GeneratedRegex(@"-(s)-\d+-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotIdPattern();

    [GeneratedRegex(@"^(deploymentprocess|variableset)-.+-s-\d+-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotFileNamePattern();
}
