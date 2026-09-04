using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport.Octopus;

public class OctopusDocumentClassifierTests
{
    [Fact]
    public void Classify_UsesManifestDocumentTypeBeforeFileName()
    {
        var entry = new OctopusManifestEntryDto
        {
            Id = "Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43",
            Name = "Next Chat",
            DocumentType = "DeploymentProcess",
            DocumentSource = "Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43.json"
        };

        var classification = OctopusDocumentClassifier.Classify(entry);

        classification.Kind.ShouldBe(OctopusDocumentKind.DeploymentProcess);
        classification.SourceId.ShouldBe(entry.Id);
        classification.ManifestDocumentType.ShouldBe("DeploymentProcess");
        classification.IsCurrentConfiguration.ShouldBeTrue();
    }

    [Fact]
    public void Classify_ManifestProjectVariablesSnapshot_AsVariableSetSnapshot()
    {
        var entry = new OctopusManifestEntryDto
        {
            Id = "variableset-Projects-1323-s-55-N6739-D4C63A85E7894C5D8C20D9297FEA1A43",
            Name = "Variables",
            DocumentType = "ProjectVariables",
            DocumentSource = "variableset-Projects-1323-s-55-N6739-D4C63A85E7894C5D8C20D9297FEA1A43.json"
        };

        var classification = OctopusDocumentClassifier.Classify(entry);

        classification.Kind.ShouldBe(OctopusDocumentKind.VariableSetSnapshot);
        classification.IsHistoricalSnapshot.ShouldBeTrue();
        classification.IsOutOfScopeHistory.ShouldBeTrue();
        classification.IsCurrentConfiguration.ShouldBeFalse();
    }

    [Fact]
    public void Classify_ManifestCurrentDeploymentProcess_AsCurrentConfiguration()
    {
        var entry = new OctopusManifestEntryDto
        {
            Id = "deploymentprocess-Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43",
            DocumentType = "DeploymentProcess",
            DocumentSource = "deploymentprocess-Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43.json"
        };

        var classification = OctopusDocumentClassifier.Classify(entry);

        classification.Kind.ShouldBe(OctopusDocumentKind.DeploymentProcess);
        classification.IsHistoricalSnapshot.ShouldBeFalse();
        classification.IsCurrentConfiguration.ShouldBeTrue();
    }

    [Theory]
    [InlineData("manifest.json", OctopusDocumentKind.Manifest, false)]
    [InlineData("Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43.json", OctopusDocumentKind.Project, false)]
    [InlineData("ProjectGroups-210-D4C63A85E7894C5D8C20D9297FEA1A43.json", OctopusDocumentKind.ProjectGroup, false)]
    [InlineData("Environments-3-D4C63A85E7894C5D8C20D9297FEA1A43.json", OctopusDocumentKind.Environment, false)]
    [InlineData("Lifecycles-302-D4C63A85E7894C5D8C20D9297FEA1A43.json", OctopusDocumentKind.Lifecycle, false)]
    [InlineData("Channels-1523-D4C63A85E7894C5D8C20D9297FEA1A43.json", OctopusDocumentKind.Channel, false)]
    [InlineData("Feeds-1083-D4C63A85E7894C5D8C20D9297FEA1A43.json", OctopusDocumentKind.Feed, false)]
    [InlineData("Teams-286-D4C63A85E7894C5D8C20D9297FEA1A43.json", OctopusDocumentKind.Team, false)]
    [InlineData("Releases-93056-D4C63A85E7894C5D8C20D9297FEA1A43.json", OctopusDocumentKind.Release, true)]
    [InlineData("Deployments-390384-D4C63A85E7894C5D8C20D9297FEA1A43.json", OctopusDocumentKind.Deployment, true)]
    [InlineData("ServerTasks-2032665-D4C63A85E7894C5D8C20D9297FEA1A43.json", OctopusDocumentKind.ServerTask, true)]
    [InlineData("WorkerPools-1-D4C63A85E7894C5D8C20D9297FEA1A43.json", OctopusDocumentKind.WorkerPool, false)]
    [InlineData("deploymentprocess-Projects-1323-s-21-8DWSK-D4C63A85E7894C5D8C20D9297FEA1A43.json", OctopusDocumentKind.DeploymentProcessSnapshot, true)]
    [InlineData("variableset-Projects-1323-s-55-N6739-D4C63A85E7894C5D8C20D9297FEA1A43.json", OctopusDocumentKind.VariableSetSnapshot, true)]
    public void ClassifyFileName_MapsKnownExportFileNames(string fileName, OctopusDocumentKind expectedKind, bool outOfScope)
    {
        var classification = OctopusDocumentClassifier.ClassifyFileName(fileName);

        classification.Kind.ShouldBe(expectedKind);
        classification.IsOutOfScopeHistory.ShouldBe(outOfScope);
    }

    [Fact]
    public void ClassifyJsonDocument_UsesDocumentTypeWhenAvailable()
    {
        var classification = OctopusDocumentClassifier.ClassifyJsonDocument(
            "ambiguous.json",
            "Feeds-1083-D4C63A85E7894C5D8C20D9297FEA1A43",
            "DockerFeed");

        classification.Kind.ShouldBe(OctopusDocumentKind.Feed);
        classification.ManifestDocumentType.ShouldBe("DockerFeed");
    }

    [Fact]
    public void ClassifyJsonDocument_UsesIdWhenDocumentTypeIsMissing()
    {
        var classification = OctopusDocumentClassifier.ClassifyJsonDocument(
            "payload.json",
            "Machines-1-D4C63A85E7894C5D8C20D9297FEA1A43");

        classification.Kind.ShouldBe(OctopusDocumentKind.Machine);
    }

    [Fact]
    public void ClassifyJsonDocument_MapsWorkerPoolDocumentType()
    {
        var classification = OctopusDocumentClassifier.ClassifyJsonDocument(
            "WorkerPools-1-D4C63A85E7894C5D8C20D9297FEA1A43.json",
            "WorkerPools-1-D4C63A85E7894C5D8C20D9297FEA1A43",
            "WorkerPool");

        classification.Kind.ShouldBe(OctopusDocumentKind.WorkerPool);
        classification.ManifestDocumentType.ShouldBe("WorkerPool");
        classification.IsCurrentConfiguration.ShouldBeTrue();
    }

    [Theory]
    [InlineData("deploymentprocess-Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43", OctopusDocumentKind.DeploymentProcess, false)]
    [InlineData("variableset-Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43", OctopusDocumentKind.VariableSet, false)]
    [InlineData("deploymentprocess-Projects-1323-s-21-8DWSK-D4C63A85E7894C5D8C20D9297FEA1A43", OctopusDocumentKind.DeploymentProcessSnapshot, true)]
    [InlineData("variableset-Projects-1323-s-55-N6739-D4C63A85E7894C5D8C20D9297FEA1A43", OctopusDocumentKind.VariableSetSnapshot, true)]
    public void ClassifyJsonDocument_MapsCurrentAndSnapshotProcessAndVariableSetIds(string id, OctopusDocumentKind expectedKind, bool expectedHistory)
    {
        var classification = OctopusDocumentClassifier.ClassifyJsonDocument("payload.json", id);

        classification.Kind.ShouldBe(expectedKind);
        classification.IsOutOfScopeHistory.ShouldBe(expectedHistory);
    }
}
