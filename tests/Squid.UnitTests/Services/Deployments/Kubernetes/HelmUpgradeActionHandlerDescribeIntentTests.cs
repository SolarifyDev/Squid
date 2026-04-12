using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Moq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Release;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// Phase 9d — verifies that <see cref="HelmUpgradeActionHandler"/> overrides
/// <c>DescribeIntentAsync</c> and emits a <see cref="HelmUpgradeIntent"/> directly, with
/// a stable semantic name (<c>helm-upgrade</c>) and every legacy action property mapped
/// onto a semantic intent field. Feed-backed charts populate the <see cref="HelmRepository"/>
/// sub-record so renderers can reconstruct the <c>helm repo add</c> flow without re-reading
/// raw properties. The legacy <c>PrepareAsync</c> path is preserved until Phase 9g.
/// </summary>
public class HelmUpgradeActionHandlerDescribeIntentTests
{
    private readonly HelmUpgradeActionHandler _handler = new();

    private static DeploymentActionDto CreateAction(
        string actionName = "deploy-helm",
        Dictionary<string, string> properties = null)
    {
        var action = new DeploymentActionDto
        {
            Name = actionName,
            ActionType = SpecialVariables.ActionTypes.HelmChartUpgrade,
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (properties == null) return action;

        foreach (var kvp in properties)
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = kvp.Key,
                PropertyValue = kvp.Value
            });

        return action;
    }

    private static ActionExecutionContext CreateContext(
        string stepName = "Upgrade Release",
        DeploymentActionDto action = null,
        List<SelectedPackageDto> selectedPackages = null) => new()
    {
        Step = new DeploymentStepDto { Name = stepName },
        Action = action ?? CreateAction(),
        SelectedPackages = selectedPackages
    };

    private static HelmUpgradeActionHandler CreateHandlerWithFeed(ExternalFeed feed)
    {
        var mock = new Mock<IExternalFeedDataProvider>();
        mock.Setup(f => f.GetFeedByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(feed);
        return new HelmUpgradeActionHandler(mock.Object);
    }

    [Fact]
    public async Task DescribeIntentAsync_ReturnsHelmUpgradeIntent()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ShouldBeOfType<HelmUpgradeIntent>();
    }

    [Fact]
    public async Task DescribeIntentAsync_NameIsHelmUpgrade()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldBe("helm-upgrade");
    }

    [Fact]
    public async Task DescribeIntentAsync_DoesNotUseLegacyName()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldNotStartWith("legacy:");
    }

    [Fact]
    public async Task DescribeIntentAsync_ReleaseName_FromProperty()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.ReleaseName"] = "my-app"
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ReleaseName.ShouldBe("my-app");
    }

    [Fact]
    public async Task DescribeIntentAsync_ReleaseName_FallsBackToActionName()
    {
        var ctx = CreateContext(action: CreateAction(actionName: "fallback-release"));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ReleaseName.ShouldBe("fallback-release");
    }

    [Fact]
    public async Task DescribeIntentAsync_ReleaseName_FallsBackToLiteralReleaseWhenNothingSet()
    {
        var action = CreateAction();
        action.Name = null;
        var ctx = CreateContext(action: action);

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ReleaseName.ShouldBe("release");
    }

    [Fact]
    public async Task DescribeIntentAsync_ChartReference_FromChartPath()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.ChartPath"] = "./charts/web"
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ChartReference.ShouldBe("./charts/web");
    }

    [Fact]
    public async Task DescribeIntentAsync_ChartReference_DefaultsToCurrentDirectory()
    {
        var ctx = CreateContext();

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ChartReference.ShouldBe(".");
    }

    [Fact]
    public async Task DescribeIntentAsync_ExplicitNamespace_UsedInIntent()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.KubernetesContainers.Namespace"] = "production"
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Namespace.ShouldBe("production");
    }

    [Fact]
    public async Task DescribeIntentAsync_NoNamespace_FallsBackToDefault()
    {
        var ctx = CreateContext();

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Namespace.ShouldBe("default");
    }

    [Fact]
    public async Task DescribeIntentAsync_CustomHelmExecutable_FromProperty()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.CustomHelmExecutable"] = "/usr/local/bin/helm3"
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.CustomHelmExecutable.ShouldBe("/usr/local/bin/helm3");
    }

    [Fact]
    public async Task DescribeIntentAsync_CustomHelmExecutable_DefaultsToEmpty()
    {
        var ctx = CreateContext();

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.CustomHelmExecutable.ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("False", false)]
    public async Task DescribeIntentAsync_ResetValues_FromProperty(string value, bool expected)
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.ResetValues"] = value
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ResetValues.ShouldBe(expected);
    }

    [Fact]
    public async Task DescribeIntentAsync_ResetValues_DefaultsToTrue()
    {
        var ctx = CreateContext();

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ResetValues.ShouldBeTrue();
    }

    [Fact]
    public async Task DescribeIntentAsync_Wait_TrueFromProperty()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.Wait"] = "True"
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Wait.ShouldBeTrue();
    }

    [Fact]
    public async Task DescribeIntentAsync_Wait_DefaultsToFalse()
    {
        var ctx = CreateContext();

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Wait.ShouldBeFalse();
    }

    [Fact]
    public async Task DescribeIntentAsync_Wait_ClientVersionPresenceDefaultsTrue()
    {
        // Legacy quirk: if ClientVersion property is present (even without Wait being set),
        // HelmWait defaults to True. Preserved on the intent to match existing behaviour.
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.ClientVersion"] = "3.12.0"
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Wait.ShouldBeTrue();
    }

    [Fact]
    public async Task DescribeIntentAsync_WaitForJobs_TrueFromProperty()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.WaitForJobs"] = "True"
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.WaitForJobs.ShouldBeTrue();
    }

    [Fact]
    public async Task DescribeIntentAsync_WaitForJobs_DefaultsToFalse()
    {
        var ctx = CreateContext();

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.WaitForJobs.ShouldBeFalse();
    }

    [Fact]
    public async Task DescribeIntentAsync_Timeout_FromProperty()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.Timeout"] = "10m"
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Timeout.ShouldBe("10m");
    }

    [Fact]
    public async Task DescribeIntentAsync_Timeout_DefaultsToEmpty()
    {
        var ctx = CreateContext();

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Timeout.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task DescribeIntentAsync_AdditionalArgs_FromProperty()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.AdditionalArgs"] = "--debug --dry-run"
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.AdditionalArgs.ShouldBe("--debug --dry-run");
    }

    [Fact]
    public async Task DescribeIntentAsync_AdditionalArgs_DefaultsToEmpty()
    {
        var ctx = CreateContext();

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.AdditionalArgs.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task DescribeIntentAsync_YamlValues_EmitsSingleValuesFile()
    {
        var yaml = "replicaCount: 3\nimage:\n  tag: v1.0";
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.YamlValues"] = yaml
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ValuesFiles.Count.ShouldBe(1);
        intent.ValuesFiles[0].RelativePath.ShouldBe("rawYamlValues.yaml");
        Encoding.UTF8.GetString(intent.ValuesFiles[0].Content).ShouldBe(yaml);
    }

    [Fact]
    public async Task DescribeIntentAsync_NoYamlValues_ValuesFilesEmpty()
    {
        var ctx = CreateContext();

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ValuesFiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task DescribeIntentAsync_KeyValues_JsonPopulatesInlineValues()
    {
        var keyValues = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["image.tag"] = "v2.0",
            ["replicaCount"] = "5"
        });
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.KeyValues"] = keyValues
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.InlineValues.Count.ShouldBe(2);
        intent.InlineValues["image.tag"].ShouldBe("v2.0");
        intent.InlineValues["replicaCount"].ShouldBe("5");
    }

    [Fact]
    public async Task DescribeIntentAsync_KeyValues_Missing_InlineValuesEmpty()
    {
        var ctx = CreateContext();

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.InlineValues.ShouldBeEmpty();
    }

    [Fact]
    public async Task DescribeIntentAsync_KeyValues_MalformedJsonFallsBackToCommaParsing()
    {
        // Legacy fallback: when KeyValues isn't valid JSON, the handler parses it as
        // comma-separated "key=value" pairs. The intent mapper must preserve this quirk.
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.KeyValues"] = "image.tag=v3.0,replicaCount=2"
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.InlineValues["image.tag"].ShouldBe("v3.0");
        intent.InlineValues["replicaCount"].ShouldBe("2");
    }

    [Fact]
    public async Task DescribeIntentAsync_NoFeed_RepositoryIsNull()
    {
        var ctx = CreateContext();

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Repository.ShouldBeNull();
    }

    [Fact]
    public async Task DescribeIntentAsync_FeedBackedChart_PopulatesRepository()
    {
        var feed = new ExternalFeed
        {
            Id = 42,
            FeedUri = "https://charts.example.com",
            Username = "feed-user",
            Password = "feed-pass"
        };
        var handler = CreateHandlerWithFeed(feed);
        var action = CreateAction(actionName: "web", properties: new Dictionary<string, string>
        {
            ["Squid.Action.Package.FeedId"] = "42",
            ["Squid.Action.Package.PackageId"] = "nginx"
        });
        var ctx = new ActionExecutionContext
        {
            Step = new DeploymentStepDto { Name = "Upgrade Release" },
            Action = action,
            SelectedPackages = new List<SelectedPackageDto>
            {
                new() { ActionName = "web", Version = "1.2.3" }
            }
        };

        var intent = (HelmUpgradeIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ChartReference.ShouldBe("squid-helm-repo/nginx");
        intent.ChartVersion.ShouldBe("1.2.3");
        intent.Repository.ShouldNotBeNull();
        intent.Repository!.Name.ShouldBe("squid-helm-repo");
        intent.Repository.Url.ShouldBe("https://charts.example.com");
        intent.Repository.Username.ShouldBe("feed-user");
        intent.Repository.Password.ShouldBe("feed-pass");
    }

    [Fact]
    public async Task DescribeIntentAsync_FeedWithoutCredentials_RepositoryHasNullAuth()
    {
        var feed = new ExternalFeed
        {
            Id = 7,
            FeedUri = "https://public-charts.example.com"
        };
        var handler = CreateHandlerWithFeed(feed);
        var action = CreateAction(actionName: "api", properties: new Dictionary<string, string>
        {
            ["Squid.Action.Package.FeedId"] = "7",
            ["Squid.Action.Package.PackageId"] = "api-chart"
        });
        var ctx = new ActionExecutionContext
        {
            Step = new DeploymentStepDto { Name = "Upgrade Release" },
            Action = action
        };

        var intent = (HelmUpgradeIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Repository.ShouldNotBeNull();
        intent.Repository!.Url.ShouldBe("https://public-charts.example.com");
        intent.Repository.Username.ShouldBeNull();
        intent.Repository.Password.ShouldBeNull();
    }

    [Fact]
    public async Task DescribeIntentAsync_FeedIdWithoutPackageId_FallsBackToLocalChartPath()
    {
        var handler = CreateHandlerWithFeed(new ExternalFeed { Id = 1, FeedUri = "https://x" });
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Package.FeedId"] = "1",
            ["Squid.Action.Helm.ChartPath"] = "./local"
        });
        var ctx = CreateContext(action: action);

        var intent = (HelmUpgradeIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ChartReference.ShouldBe("./local");
        intent.Repository.ShouldBeNull();
        intent.ChartVersion.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task DescribeIntentAsync_InvalidFeedId_FallsBackToLocalChartPath()
    {
        var handler = CreateHandlerWithFeed(new ExternalFeed { Id = 1, FeedUri = "https://x" });
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Package.FeedId"] = "not-a-number",
            ["Squid.Action.Package.PackageId"] = "nginx",
            ["Squid.Action.Helm.ChartPath"] = "./local"
        });
        var ctx = CreateContext(action: action);

        var intent = (HelmUpgradeIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ChartReference.ShouldBe("./local");
        intent.Repository.ShouldBeNull();
    }

    [Fact]
    public async Task DescribeIntentAsync_PopulatesStepAndActionName()
    {
        var ctx = CreateContext(stepName: "Upgrade Prod", action: CreateAction(actionName: "prod-helm"));

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.StepName.ShouldBe("Upgrade Prod");
        intent.ActionName.ShouldBe("prod-helm");
    }

    [Fact]
    public async Task DescribeIntentAsync_NullContext_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => ((IActionHandler)_handler).DescribeIntentAsync(null!, CancellationToken.None));
    }

    // ========== ValueSources: multi-source support ==========

    [Fact]
    public async Task DescribeIntentAsync_ValueSources_SingleInlineYaml_EmitsValuesFile()
    {
        var sources = JsonSerializer.Serialize(new[]
        {
            new { Type = "InlineYaml", Value = "replicaCount: 5" }
        });
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            [KubernetesHelmProperties.ValueSources] = sources
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ValuesFiles.Count.ShouldBe(1);
        intent.ValuesFiles[0].RelativePath.ShouldBe("values-0.yaml");
        Encoding.UTF8.GetString(intent.ValuesFiles[0].Content).ShouldBe("replicaCount: 5");
    }

    [Fact]
    public async Task DescribeIntentAsync_ValueSources_MultipleInlineYaml_EmitsMultipleFiles()
    {
        var sources = JsonSerializer.Serialize(new[]
        {
            new { Type = "InlineYaml", Value = "replicaCount: 3" },
            new { Type = "InlineYaml", Value = "image:\n  tag: v2" }
        });
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            [KubernetesHelmProperties.ValueSources] = sources
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ValuesFiles.Count.ShouldBe(2);
        intent.ValuesFiles[0].RelativePath.ShouldBe("values-0.yaml");
        intent.ValuesFiles[1].RelativePath.ShouldBe("values-1.yaml");
    }

    [Fact]
    public async Task DescribeIntentAsync_ValueSources_KeyValues_MergedIntoInlineValues()
    {
        var kvJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["image.tag"] = "v3.0",
            ["replicaCount"] = "7"
        });
        var sources = JsonSerializer.Serialize(new[]
        {
            new { Type = "KeyValues", Value = kvJson }
        });
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            [KubernetesHelmProperties.ValueSources] = sources
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.InlineValues["image.tag"].ShouldBe("v3.0");
        intent.InlineValues["replicaCount"].ShouldBe("7");
    }

    [Fact]
    public async Task DescribeIntentAsync_ValueSources_TakesPrecedenceOverYamlValues()
    {
        var sources = JsonSerializer.Serialize(new[]
        {
            new { Type = "InlineYaml", Value = "from: valueSources" }
        });
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            [KubernetesHelmProperties.ValueSources] = sources,
            [KubernetesHelmProperties.YamlValues] = "from: yamlValues"
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ValuesFiles.Count.ShouldBe(1);
        Encoding.UTF8.GetString(intent.ValuesFiles[0].Content).ShouldBe("from: valueSources");
        intent.ValuesFiles[0].RelativePath.ShouldBe("values-0.yaml");
    }

    [Fact]
    public async Task DescribeIntentAsync_ValueSources_EmptyValue_Skipped()
    {
        var sources = JsonSerializer.Serialize(new[]
        {
            new { Type = "InlineYaml", Value = "" },
            new { Type = "InlineYaml", Value = "valid: true" }
        });
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            [KubernetesHelmProperties.ValueSources] = sources
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ValuesFiles.Count.ShouldBe(1);
        intent.ValuesFiles[0].RelativePath.ShouldBe("values-0.yaml");
    }

    [Fact]
    public async Task DescribeIntentAsync_ValueSources_InvalidJson_ReturnsEmpty()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            [KubernetesHelmProperties.ValueSources] = "not-valid-json{"
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ValuesFiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task DescribeIntentAsync_ValueSources_MixedTypes_FilesAndInlineValues()
    {
        var kvJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["env"] = "prod" });
        var sources = JsonSerializer.Serialize(new[]
        {
            new { Type = "InlineYaml", Value = "replicaCount: 3" },
            new { Type = "KeyValues", Value = kvJson }
        });
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            [KubernetesHelmProperties.ValueSources] = sources
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ValuesFiles.Count.ShouldBe(1);
        intent.ValuesFiles[0].RelativePath.ShouldBe("values-0.yaml");
        intent.InlineValues["env"].ShouldBe("prod");
    }

    [Fact]
    public async Task DescribeIntentAsync_ValueSources_KeyValuesMergedWithKeyValuesProperty()
    {
        var vsKeyValues = JsonSerializer.Serialize(new Dictionary<string, string> { ["from"] = "valueSources" });
        var sources = JsonSerializer.Serialize(new[]
        {
            new { Type = "KeyValues", Value = vsKeyValues }
        });
        var propKeyValues = JsonSerializer.Serialize(new Dictionary<string, string> { ["from2"] = "property" });
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            [KubernetesHelmProperties.ValueSources] = sources,
            [KubernetesHelmProperties.KeyValues] = propKeyValues
        }));

        var intent = (HelmUpgradeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.InlineValues["from"].ShouldBe("valueSources");
        intent.InlineValues["from2"].ShouldBe("property");
    }
}
