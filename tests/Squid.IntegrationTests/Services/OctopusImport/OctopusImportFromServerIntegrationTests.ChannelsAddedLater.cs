using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Channels;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// A channel added in Octopus after its project was imported is linked to the channel of that name the user already made in Squid:
/// the Octopus history lands on it, and no second channel of that name is created.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    private const string HotfixChannel = "Hotfix";

    [Fact]
    public async Task ChannelAddedInOctopusLater_SameNameAsChannelMadeInSquid_ReimportLinksIt_NeverDuplicates()
    {
        await using var octopus = OneDeployment();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var project = await SingleAsync<Project>();
        var made = await AddChannelInSquidAsync(project, HotfixChannel);

        AddOctopusHotfixChannelWithHistory(octopus);
        var reimport = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(reimport);
        (await ChannelNamesAsync(project.Id)).Count(n => n == HotfixChannel).ShouldBe(1, $"never duplicates a channel the user already has: {Describe(reimport.PreviewPlan)}");
        reimport.PreviewPlan.Resources.Single(r => r.SourceId == "Channels-2").PreviewAction.ShouldBe(OctopusImportPreviewAction.ReuseExisting);
        (await Run<SquidDbContext, Release>(db => db.Set<Release>().AsNoTracking().SingleAsync(r => r.Version == "2.0.0"))).ChannelId.ShouldBe(made.Id, "the Octopus history lands on the channel the user made");

        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        (await ChannelNamesAsync(project.Id)).Count(n => n == HotfixChannel).ShouldBe(1, "a later import keeps the link");
    }

    // Octopus renamed the imported Hotfix channel and made a new channel of the old name: the Squid channel the import created belongs
    // to the first one (it keeps its name, config changes are not applied), so the new channel and its history are never merged onto it.
    [Fact]
    public async Task ImportedChannelRenamedInOctopus_NewChannelTakesItsName_CreatedNotMergedOntoTheImportedOne()
    {
        await using var octopus = OneDeployment();
        AddOctopusHotfixChannelWithHistory(octopus);
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var project = await SingleAsync<Project>();
        var importedHotfix = await Run<SquidDbContext, Channel>(db => db.Set<Channel>().AsNoTracking().SingleAsync(c => c.ProjectId == project.Id && c.Name == HotfixChannel));

        var channels = (List<Dictionary<string, object>>)octopus.Resources["projects/Projects-1/channels"];
        channels.Single(c => (string)c["Id"] == "Channels-2")["Name"] = "Hotfix-old";
        channels.Add(new Dictionary<string, object> { ["Id"] = "Channels-3", ["Name"] = HotfixChannel, ["Slug"] = "hotfix-new", ["ProjectId"] = "Projects-1", ["IsDefault"] = false });
        octopus.Release("Projects-1", "Releases-3", "3.0.0").Deployment("Projects-1", "Deployments-3", "Releases-3", "ServerTasks-3");
        octopus.Releases["Projects-1"].Single(r => (string)r["Id"] == "Releases-3")["ChannelId"] = "Channels-3";
        octopus.Deployments["Projects-1"].Single(d => (string)d["Id"] == "Deployments-3")["ChannelId"] = "Channels-3";

        var reimport = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(reimport);
        reimport.PreviewPlan.Resources.Single(r => r.SourceId == "Channels-3").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create, Describe(reimport.PreviewPlan));
        var release = await Run<SquidDbContext, Release>(db => db.Set<Release>().AsNoTracking().SingleAsync(r => r.Version == "3.0.0"));
        release.ChannelId.ShouldNotBe(importedHotfix.Id, "another Octopus channel's history was merged onto the channel the import created for Channels-2");
        (await Run<SquidDbContext, int>(db => db.Set<Release>().CountAsync(r => r.ChannelId == importedHotfix.Id))).ShouldBe(1, "the imported Hotfix channel keeps only its own release");
    }

    private static void AddOctopusHotfixChannelWithHistory(StubOctopusServer octopus)
    {
        ((List<Dictionary<string, object>>)octopus.Resources["projects/Projects-1/channels"]).Add(new Dictionary<string, object> { ["Id"] = "Channels-2", ["Name"] = HotfixChannel, ["Slug"] = "hotfix", ["ProjectId"] = "Projects-1", ["IsDefault"] = false });
        octopus.Release("Projects-1", "Releases-2", "2.0.0").Deployment("Projects-1", "Deployments-2", "Releases-2", "ServerTasks-2");
        octopus.Releases["Projects-1"].Single(r => (string)r["Id"] == "Releases-2")["ChannelId"] = "Channels-2";
        octopus.Deployments["Projects-1"].Single(d => (string)d["Id"] == "Deployments-2")["ChannelId"] = "Channels-2";
    }

    private async Task<Channel> AddChannelInSquidAsync(Project project, string name)
    {
        var channel = new Channel { Name = name, Slug = name.ToLowerInvariant(), ProjectId = project.Id, SpaceId = project.SpaceId, LifecycleId = project.LifecycleId, IsDefault = false };

        await Run<IChannelDataProvider>(channels => channels.AddChannelAsync(channel));

        return channel;
    }

    private Task<List<string>> ChannelNamesAsync(int projectId)
        => Run<SquidDbContext, List<string>>(db => db.Set<Channel>().AsNoTracking().Where(c => c.ProjectId == projectId).Select(c => c.Name).ToListAsync());
}
