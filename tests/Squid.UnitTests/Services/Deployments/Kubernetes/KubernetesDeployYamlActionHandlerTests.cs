using System.Collections.Generic;
using System.Text;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesDeployYamlActionHandlerTests
{
    private readonly KubernetesDeployYamlActionHandler _handler = new();

    private static DeploymentActionDto CreateAction(
        string actionType = "Squid.KubernetesDeployRawYaml",
        string inlineYaml = null,
        string syntax = null,
        string feedId = null,
        string packageId = null)
    {
        var action = new DeploymentActionDto
        {
            Name = "DeployYaml",
            ActionType = actionType,
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (inlineYaml != null)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesYaml.InlineYaml",
                PropertyValue = inlineYaml
            });
        }

        if (syntax != null)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.Script.Syntax",
                PropertyValue = syntax
            });
        }

        if (feedId != null)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = SpecialVariables.Action.PackageFeedId,
                PropertyValue = feedId
            });
        }

        if (packageId != null)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = SpecialVariables.Action.PackageId,
                PropertyValue = packageId
            });
        }

        return action;
    }

    private static ActionExecutionContext CreateContext(DeploymentActionDto action, List<SelectedPackageDto> selectedPackages = null, List<VariableDto> variables = null) => new()
    {
        Action = action,
        SelectedPackages = selectedPackages ?? new List<SelectedPackageDto>(),
        Variables = variables ?? new List<VariableDto>()
    };

    private static KubernetesDeployYamlActionHandler CreateHandlerWithFeed(ExternalFeed feed, Dictionary<string, byte[]> fetchedFiles = null, List<string> fetcherWarnings = null)
    {
        var feedMock = new Mock<IExternalFeedDataProvider>();
        feedMock.Setup(f => f.GetFeedByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(feed);

        var fetcherMock = new Mock<IPackageContentFetcher>();
        fetcherMock.Setup(f => f.FetchAsync(It.IsAny<ExternalFeed>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(fetchedFiles ?? new Dictionary<string, byte[]>(), fetcherWarnings ?? new List<string>(), Array.Empty<byte>()));

        return new KubernetesDeployYamlActionHandler(feedMock.Object, fetcherMock.Object);
    }

    private static KubernetesDeployYamlActionHandler CreateHandlerWithMocks(out Mock<IExternalFeedDataProvider> feedMock, out Mock<IPackageContentFetcher> fetcherMock)
    {
        feedMock = new Mock<IExternalFeedDataProvider>();
        fetcherMock = new Mock<IPackageContentFetcher>();
        return new KubernetesDeployYamlActionHandler(feedMock.Object, fetcherMock.Object);
    }

    // === CanHandle Tests (default interface implementation) ===

    [Fact]
    public void CanHandle_MatchingActionType_ReturnsTrue()
    {
        var action = CreateAction("Squid.KubernetesDeployRawYaml");
        ((IActionHandler)_handler).CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_CaseInsensitive_ReturnsTrue()
    {
        var action = CreateAction("squid.kubernetesdeployrawyaml");
        ((IActionHandler)_handler).CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_DifferentActionType_ReturnsFalse()
    {
        var action = CreateAction("Squid.Script");
        ((IActionHandler)_handler).CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullAction_ReturnsFalse()
    {
        ((IActionHandler)_handler).CanHandle(null).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullActionType_ReturnsFalse()
    {
        var action = new DeploymentActionDto { ActionType = null };
        ((IActionHandler)_handler).CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void ActionType_ReturnsExpectedValue()
    {
        _handler.ActionType.ShouldBe(SpecialVariables.ActionTypes.KubernetesDeployRawYaml);
    }

    // === PrepareAsync — Inline YAML Tests ===

    [Fact]
    public async Task PrepareAsync_InlineYaml_CreatesYamlFile()
    {
        var yaml = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: test";
        var action = CreateAction(inlineYaml: yaml);
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldContainKey("inline-deployment.yaml");
        var fileContent = Encoding.UTF8.GetString(result.Files["inline-deployment.yaml"]);
        fileContent.ShouldBe(yaml);
    }

    [Fact]
    public async Task PrepareAsync_InlineYaml_Bash_GeneratesApplyCommand()
    {
        var yaml = "apiVersion: v1\nkind: Pod";
        var action = CreateAction(inlineYaml: yaml, syntax: "Bash");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("kubectl apply -f");
        result.ScriptBody.ShouldContain("./inline-deployment.yaml");
    }

    [Fact]
    public async Task PrepareAsync_InlineYaml_PowerShell_GeneratesApplyCommand()
    {
        var yaml = "apiVersion: v1\nkind: Pod";
        var action = CreateAction(inlineYaml: yaml, syntax: "PowerShell");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("kubectl apply -f");
        result.ScriptBody.ShouldContain(".\\inline-deployment.yaml");
    }

    // === PrepareAsync — No Inline YAML (Content Dir) Tests ===

    [Fact]
    public async Task PrepareAsync_NoInlineYaml_Bash_AppliesContentDir()
    {
        var action = CreateAction(syntax: "Bash");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("kubectl apply -f \"./content/\"");
        result.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_NoInlineYaml_PowerShell_AppliesContentDir()
    {
        var action = CreateAction(syntax: "PowerShell");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("kubectl apply -f \".\\content\\\"");
        result.Files.ShouldBeEmpty();
    }

    // === General Result Tests ===

    [Fact]
    public async Task PrepareAsync_CalamariCommand_IsNull()
    {
        var action = CreateAction(inlineYaml: "apiVersion: v1");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.CalamariCommand.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_DefaultSyntax_IsBash()
    {
        var action = CreateAction(inlineYaml: "apiVersion: v1");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    [Fact]
    public async Task PrepareAsync_EmptyInlineYaml_TreatedAsNoYaml()
    {
        var action = CreateAction(inlineYaml: "   ");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        // Whitespace-only is treated as no inline YAML — falls back to content dir
        result.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_MultiDocumentYaml_PreservesAll()
    {
        var yaml = "apiVersion: v1\nkind: Service\n---\napiVersion: v1\nkind: Deployment";
        var action = CreateAction(inlineYaml: yaml);
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        var fileContent = Encoding.UTF8.GetString(result.Files["inline-deployment.yaml"]);
        fileContent.ShouldContain("---");
        fileContent.ShouldContain("Service");
        fileContent.ShouldContain("Deployment");
    }

    [Fact]
    public async Task PrepareAsync_NullProperties_FallsBackToContentDir()
    {
        var action = new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployRawYaml",
            Properties = null
        };
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldBeEmpty();
        result.ScriptBody.ShouldContain("kubectl apply -f");
    }

    [Fact]
    public async Task PrepareAsync_BashSyntax_SetsSyntaxToBash()
    {
        var action = CreateAction(inlineYaml: "apiVersion: v1", syntax: "Bash");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    // === Server-Side Apply Tests ===

    [Fact]
    public async Task PrepareAsync_ServerSideApplyEnabled_GeneratedScriptContainsServerSideFlag()
    {
        var action = CreateAction(inlineYaml: "apiVersion: v1\nkind: Pod", syntax: "Bash");
        action.Properties.Add(new DeploymentActionPropertyDto
        {
            PropertyName = "Squid.Action.Kubernetes.ServerSideApply.Enabled",
            PropertyValue = "True"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("--server-side");
        result.ScriptBody.ShouldContain("--field-manager=\"squid-deploy\"");
    }

    [Fact]
    public async Task PrepareAsync_ServerSideApplyDisabled_NoServerSideFlag()
    {
        var action = CreateAction(inlineYaml: "apiVersion: v1\nkind: Pod", syntax: "Bash");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldNotContain("--server-side");
    }

    // === Package Source — Backward Compatibility ===

    [Fact]
    public async Task PrepareAsync_InlineYaml_IgnoresPackage()
    {
        var feed = new ExternalFeed { Id = 1, FeedUri = "https://example.com", FeedType = "Generic" };
        var fetchedFiles = new Dictionary<string, byte[]> { ["deployment.yaml"] = Encoding.UTF8.GetBytes("apiVersion: v1") };
        var handler = CreateHandlerWithFeed(feed, fetchedFiles);

        var action = CreateAction(inlineYaml: "apiVersion: v1\nkind: Inline", syntax: "Bash", feedId: "1", packageId: "my-pkg");
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        // InlineYaml takes priority — package files ignored
        result.Files.ShouldContainKey("inline-deployment.yaml");
        result.Files.ShouldNotContainKey("content/deployment.yaml");
    }

    [Fact]
    public async Task PrepareAsync_NullProviders_FallsBackToContentDir()
    {
        // new() without DI → both providers null
        var handler = new KubernetesDeployYamlActionHandler();
        var action = CreateAction(syntax: "Bash", feedId: "1", packageId: "my-pkg");
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldBeEmpty();
        result.ScriptBody.ShouldContain("kubectl apply -f \"./content/\"");
    }

    // === Package Source — Fallback Chain ===

    [Fact]
    public async Task PrepareAsync_EmptyFeedId_FallsBack()
    {
        var handler = CreateHandlerWithFeed(new ExternalFeed { Id = 1 });
        var action = CreateAction(syntax: "Bash", feedId: "", packageId: "my-pkg");
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldBeEmpty();
        result.ScriptBody.ShouldContain("./content/");
    }

    [Fact]
    public async Task PrepareAsync_InvalidFeedId_FallsBack()
    {
        var handler = CreateHandlerWithFeed(new ExternalFeed { Id = 1 });
        var action = CreateAction(syntax: "Bash", feedId: "abc", packageId: "my-pkg");
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_FeedIdButNoPackageId_FallsBack()
    {
        var handler = CreateHandlerWithFeed(new ExternalFeed { Id = 1 });
        var action = CreateAction(syntax: "Bash", feedId: "1");
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_FeedNotFound_FallsBack()
    {
        var feedMock = new Mock<IExternalFeedDataProvider>();
        feedMock.Setup(f => f.GetFeedByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((ExternalFeed)null);
        var fetcherMock = new Mock<IPackageContentFetcher>();
        var handler = new KubernetesDeployYamlActionHandler(feedMock.Object, fetcherMock.Object);

        var action = CreateAction(syntax: "Bash", feedId: "99", packageId: "my-pkg");
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldBeEmpty();
    }

    // === Package Source — Happy Path ===

    [Fact]
    public async Task PrepareAsync_Package_Bash_FilesInContentSubdir()
    {
        var feed = new ExternalFeed { Id = 1, FeedUri = "https://example.com", FeedType = "Generic" };
        var fetchedFiles = new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = Encoding.UTF8.GetBytes("apiVersion: v1\nkind: Deployment"),
            ["service.yaml"] = Encoding.UTF8.GetBytes("apiVersion: v1\nkind: Service")
        };
        var handler = CreateHandlerWithFeed(feed, fetchedFiles);

        var action = CreateAction(syntax: "Bash", feedId: "1", packageId: "my-pkg");
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldContainKey("content/deployment.yaml");
        result.Files.ShouldContainKey("content/service.yaml");
        result.Files.Count.ShouldBe(2);
    }

    [Fact]
    public async Task PrepareAsync_Package_Bash_AppliesContentDir()
    {
        var feed = new ExternalFeed { Id = 1, FeedUri = "https://example.com", FeedType = "Generic" };
        var fetchedFiles = new Dictionary<string, byte[]> { ["deployment.yaml"] = Encoding.UTF8.GetBytes("v1") };
        var handler = CreateHandlerWithFeed(feed, fetchedFiles);

        var action = CreateAction(syntax: "Bash", feedId: "1", packageId: "my-pkg");
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("kubectl apply -f \"./content/\"");
    }

    [Fact]
    public async Task PrepareAsync_Package_PowerShell_AppliesContentDir()
    {
        var feed = new ExternalFeed { Id = 1, FeedUri = "https://example.com", FeedType = "Generic" };
        var fetchedFiles = new Dictionary<string, byte[]> { ["deployment.yaml"] = Encoding.UTF8.GetBytes("v1") };
        var handler = CreateHandlerWithFeed(feed, fetchedFiles);

        var action = CreateAction(syntax: "PowerShell", feedId: "1", packageId: "my-pkg");
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("kubectl apply -f \".\\content\\\"");
    }

    [Fact]
    public async Task PrepareAsync_Package_PassesFeedToFetcher()
    {
        var feed = new ExternalFeed { Id = 5, FeedUri = "https://example.com", FeedType = "Generic" };
        var handler = CreateHandlerWithMocks(out var feedMock, out var fetcherMock);
        feedMock.Setup(f => f.GetFeedByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(feed);
        var files = new Dictionary<string, byte[]> { ["deploy.yaml"] = Encoding.UTF8.GetBytes("v1") };
        fetcherMock.Setup(f => f.FetchAsync(feed, "my-pkg", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(files, new List<string>(), Array.Empty<byte>()));

        var action = CreateAction(syntax: "Bash", feedId: "5", packageId: "my-pkg");
        var ctx = CreateContext(action);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        fetcherMock.Verify(f => f.FetchAsync(feed, "my-pkg", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PrepareAsync_Package_WithServerSideApply()
    {
        var feed = new ExternalFeed { Id = 1, FeedUri = "https://example.com", FeedType = "Generic" };
        var fetchedFiles = new Dictionary<string, byte[]> { ["deployment.yaml"] = Encoding.UTF8.GetBytes("v1") };
        var handler = CreateHandlerWithFeed(feed, fetchedFiles);

        var action = CreateAction(syntax: "Bash", feedId: "1", packageId: "my-pkg");
        action.Properties.Add(new DeploymentActionPropertyDto
        {
            PropertyName = "Squid.Action.Kubernetes.ServerSideApply.Enabled",
            PropertyValue = "True"
        });
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("--server-side");
    }

    // === Version Resolution ===

    [Fact]
    public async Task PrepareAsync_Package_SelectedVersion_PassedToFetcher()
    {
        var feed = new ExternalFeed { Id = 1, FeedUri = "https://example.com", FeedType = "Generic" };
        var handler = CreateHandlerWithMocks(out var feedMock, out var fetcherMock);
        feedMock.Setup(f => f.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);
        var files = new Dictionary<string, byte[]> { ["deploy.yaml"] = Encoding.UTF8.GetBytes("v1") };
        fetcherMock.Setup(f => f.FetchAsync(feed, "my-pkg", "3.2.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(files, new List<string>(), Array.Empty<byte>()));

        var action = CreateAction(syntax: "Bash", feedId: "1", packageId: "my-pkg");
        var selectedPackages = new List<SelectedPackageDto>
        {
            new() { ActionName = "DeployYaml", PackageReferenceName = "", Version = "3.2.1" }
        };
        var ctx = CreateContext(action, selectedPackages);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        fetcherMock.Verify(f => f.FetchAsync(feed, "my-pkg", "3.2.1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PrepareAsync_Package_VariableVersion_PassedToFetcher()
    {
        var feed = new ExternalFeed { Id = 1, FeedUri = "https://example.com", FeedType = "Generic" };
        var handler = CreateHandlerWithMocks(out var feedMock, out var fetcherMock);
        feedMock.Setup(f => f.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);
        var files = new Dictionary<string, byte[]> { ["deploy.yaml"] = Encoding.UTF8.GetBytes("v1") };
        fetcherMock.Setup(f => f.FetchAsync(feed, "my-pkg", "2.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(files, new List<string>(), Encoding.UTF8.GetBytes("v1")));

        var action = CreateAction(syntax: "Bash", feedId: "1", packageId: "my-pkg");
        var variables = new List<VariableDto>
        {
            new() { Name = SpecialVariables.Action.PackageVersion, Value = "2.0.0" }
        };
        var ctx = CreateContext(action, variables: variables);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        fetcherMock.Verify(f => f.FetchAsync(feed, "my-pkg", "2.0.0", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PrepareAsync_Package_NoVersion_EmptyToFetcher()
    {
        var feed = new ExternalFeed { Id = 1, FeedUri = "https://example.com", FeedType = "Generic" };
        var handler = CreateHandlerWithMocks(out var feedMock, out var fetcherMock);
        feedMock.Setup(f => f.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);
        var files = new Dictionary<string, byte[]> { ["deploy.yaml"] = Encoding.UTF8.GetBytes("v1") };
        fetcherMock.Setup(f => f.FetchAsync(feed, "my-pkg", string.Empty, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(files, new List<string>(), Encoding.UTF8.GetBytes("v1")));

        var action = CreateAction(syntax: "Bash", feedId: "1", packageId: "my-pkg");
        var ctx = CreateContext(action);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        fetcherMock.Verify(f => f.FetchAsync(feed, "my-pkg", string.Empty, It.IsAny<CancellationToken>()), Times.Once);
    }

    // === Warnings Propagation ===

    [Fact]
    public async Task PrepareAsync_FetcherWarnings_PropagatedToResult()
    {
        var feed = new ExternalFeed { Id = 1, FeedUri = "https://example.com", FeedType = "Generic" };
        var fetchedFiles = new Dictionary<string, byte[]> { ["deployment.yaml"] = Encoding.UTF8.GetBytes("v1") };
        var warnings = new List<string> { "Package download was slow", "Some files skipped" };
        var handler = CreateHandlerWithFeed(feed, fetchedFiles, warnings);

        var action = CreateAction(syntax: "Bash", feedId: "1", packageId: "my-pkg");
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.Warnings.Count.ShouldBe(2);
        result.Warnings.ShouldContain("Package download was slow");
    }

    [Fact]
    public async Task PrepareAsync_EmptyFetchResult_ThrowsWithWarnings()
    {
        var feed = new ExternalFeed { Id = 1, FeedUri = "https://example.com", FeedType = "Generic" };
        var warnings = new List<string> { "Package download failed: HTTP 404" };
        var handler = CreateHandlerWithFeed(feed, new Dictionary<string, byte[]>(), warnings);

        var action = CreateAction(syntax: "Bash", feedId: "1", packageId: "my-pkg");
        var ctx = CreateContext(action);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => handler.PrepareAsync(ctx, CancellationToken.None));

        ex.Message.ShouldContain("no deployable files");
        ex.Message.ShouldContain("HTTP 404");
    }

    [Fact]
    public async Task PrepareAsync_EmptyFetchResultNoWarnings_ThrowsGenericMessage()
    {
        var feed = new ExternalFeed { Id = 1, FeedUri = "https://example.com", FeedType = "Generic" };
        var handler = CreateHandlerWithFeed(feed, new Dictionary<string, byte[]>());

        var action = CreateAction(syntax: "Bash", feedId: "1", packageId: "my-pkg");
        var ctx = CreateContext(action);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => handler.PrepareAsync(ctx, CancellationToken.None));

        ex.Message.ShouldContain("no YAML files found in package");
    }

    [Fact]
    public async Task PrepareAsync_HelmFeed_ThrowsWithActionTypeSuggestion()
    {
        var feed = new ExternalFeed { Id = 1, FeedUri = "https://charts.example.com", FeedType = "Helm" };
        var handler = CreateHandlerWithFeed(feed);

        var action = CreateAction(syntax: "Bash", feedId: "1", packageId: "my-chart");
        var ctx = CreateContext(action);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => handler.PrepareAsync(ctx, CancellationToken.None));

        ex.Message.ShouldContain("Helm Chart Upgrade");
        ex.Message.ShouldContain("Helm chart repository");
    }

    // === Theory — Source Resolution ===

    [Theory]
    [InlineData(true, false, false, "inline-deployment.yaml")]
    [InlineData(false, true, true, "content/")]
    [InlineData(false, false, false, "content/")]
    [InlineData(true, true, true, "inline-deployment.yaml")]
    public async Task PrepareAsync_SourceResolution(bool hasInlineYaml, bool hasFeedId, bool hasPackageId, string expectedPathFragment)
    {
        var feed = new ExternalFeed { Id = 1, FeedUri = "https://example.com", FeedType = "Generic" };
        var fetchedFiles = new Dictionary<string, byte[]> { ["deploy.yaml"] = Encoding.UTF8.GetBytes("v1") };
        var handler = CreateHandlerWithFeed(feed, fetchedFiles);

        var action = CreateAction(
            syntax: "Bash",
            inlineYaml: hasInlineYaml ? "apiVersion: v1" : null,
            feedId: hasFeedId ? "1" : null,
            packageId: hasPackageId ? "my-pkg" : null);
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(expectedPathFragment);
    }
}
