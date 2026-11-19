using System.Security.Cryptography;
using System.Text.Json;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Builds small, real Octopus "Export Projects" archives (manifest + one JSON document per resource) for
/// the export-password tests. Sensitive values are encrypted with an independent reference
/// implementation of the Octopus export cipher — deliberately not shared with production code.
/// </summary>
internal static class OctopusExportSecretsArchive
{
    /// <param name="projectName">Unique per test: the destination database is shared by the test class.</param>
    /// <param name="sensitiveValue">Plaintext of the project's sensitive variable; <c>null</c> = an export without secrets.</param>
    /// <param name="exportPassword">Password the sensitive value (and the feed password) is encrypted with.</param>
    /// <param name="feedPassword">Plaintext of a Docker feed's password; <c>null</c> = no feed in the export.</param>
    public static string Write(string projectName, string sensitiveValue = null, string exportPassword = null, string feedPassword = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string Id(string prefix) => $"{prefix}-{suffix}";

        var variables = new List<object> { new { Id = Guid.NewGuid().ToString(), Name = "Plain", Value = "visible", Scope = new Dictionary<string, string[]>() } };

        if (sensitiveValue != null)
            variables.Add(new { Id = Guid.NewGuid().ToString(), Name = "Db:Password", IsSensitive = true, Type = "Sensitive", Value = Encrypt(sensitiveValue, exportPassword), Scope = new Dictionary<string, string[]>() });

        var step = new
        {
            Id = Id("Steps-1"),
            Name = "Say hello",
            Condition = "Success",
            StartTrigger = "StartAfterPrevious",
            PackageRequirement = "LetOctopusDecide",
            Properties = new Dictionary<string, string>(),
            Actions = new[]
            {
                new
                {
                    Id = Id("Actions-1"), Name = "Say hello", ActionType = "Octopus.Script", IsRequired = false, IsDisabled = false,
                    Environments = Array.Empty<string>(), ExcludedEnvironments = Array.Empty<string>(), Channels = Array.Empty<string>(), Packages = Array.Empty<object>(),
                    Properties = new Dictionary<string, string> { ["Octopus.Action.Script.ScriptSource"] = "Inline", ["Octopus.Action.Script.Syntax"] = "Bash", ["Octopus.Action.Script.ScriptBody"] = "echo hello", ["Octopus.Action.RunOnServer"] = "true" }
                }
            }
        };

        var documents = new List<(string Id, string DocumentType, string DocumentSource, string Json)>
        {
            Doc(Id("ProjectGroups-1"), "ProjectGroup", new { Id = Id("ProjectGroups-1"), Name = $"Group {suffix}", Slug = $"group-{suffix}" }),
            Doc(Id("Environments-1"), "StaticDeploymentEnvironment", new { Id = Id("Environments-1"), Name = $"Env {suffix}", Slug = $"env-{suffix}" }),
            Doc(Id("Lifecycles-1"), "Lifecycle", new { Id = Id("Lifecycles-1"), Name = $"Lifecycle {suffix}", Slug = $"lc-{suffix}", Phases = new[] { new { Id = Id("Phases-1"), Name = "Only", AutomaticDeploymentTargets = new[] { Id("Environments-1") }, OptionalDeploymentTargets = Array.Empty<string>() } } }),
            Doc(Id("Projects-1"), "Project", new { Id = Id("Projects-1"), Name = projectName, Slug = $"project-{suffix}", ProjectGroupId = Id("ProjectGroups-1"), LifecycleId = Id("Lifecycles-1"), VariableSetId = Id("variableset-Projects-1"), DeploymentProcessId = Id("deploymentprocess-Projects-1"), IncludedLibraryVariableSetIds = Array.Empty<string>() }),
            Doc(Id("Channels-1"), "Channel", new { Id = Id("Channels-1"), Name = "Default", ProjectId = Id("Projects-1"), LifecycleId = (string)null, IsDefault = true, Slug = "default" }),
            Doc(Id("variableset-Projects-1"), "ProjectVariables", new { Id = Id("variableset-Projects-1"), OwnerId = Id("Projects-1"), OwnerType = "Project", Variables = variables }),
            Doc(Id("deploymentprocess-Projects-1"), "DeploymentProcess", new { Id = Id("deploymentprocess-Projects-1"), OwnerId = Id("Projects-1"), Steps = new[] { step } })
        };

        if (feedPassword != null)
            documents.Add(Doc(Id("Feeds-1"), "DockerFeed", new { Id = Id("Feeds-1"), Name = FeedName(projectName), Slug = $"feed-{suffix}", FeedType = "Docker", FeedUri = "https://registry.example.test", ApiVersion = "v2", Username = "robot", Password = Encrypt(feedPassword, exportPassword) }));

        return WriteArchive(documents);
    }

    public static string FeedName(string projectName) => $"Feed {projectName}";

    public static string Encrypt(string plaintext, string password)
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

    private static (string Id, string DocumentType, string DocumentSource, string Json) Doc(string id, string documentType, object document)
        => (id, documentType, $"{id}.json", JsonSerializer.Serialize(document));

    private static string WriteArchive(IReadOnlyList<(string Id, string DocumentType, string DocumentSource, string Json)> documents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"squid-octopus-secrets-{Guid.NewGuid():N}.zip");
        var manifest = JsonSerializer.Serialize(new
        {
            SchemaVersions = Array.Empty<string>(),
            Entries = documents.Select(d => new { d.Id, Name = d.Id, d.DocumentType, ExportType = "FullDocument", d.DocumentSource, ParentId = (string)null, Hash = Sha1(d.Json) })
        });

        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "manifest.json", manifest);

            foreach (var document in documents)
                WriteEntry(archive, document.DocumentSource, document.Json);
        }

        return path;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static string Sha1(string value) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
