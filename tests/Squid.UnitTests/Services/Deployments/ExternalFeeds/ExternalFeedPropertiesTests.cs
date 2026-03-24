using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

public class ExternalFeedPropertiesTests
{
    [Fact]
    public void GetString_ValidJson_ReturnsValue()
    {
        var feed = new ExternalFeed { Properties = """{"ApiVersion":"v2","RegistryPath":"library"}""" };

        ExternalFeedProperties.GetString(feed, "ApiVersion").ShouldBe("v2");
        ExternalFeedProperties.GetString(feed, "RegistryPath").ShouldBe("library");
    }

    [Fact]
    public void GetString_NullProperties_ReturnsNull()
    {
        var feed = new ExternalFeed { Properties = null };

        ExternalFeedProperties.GetString(feed, "ApiVersion").ShouldBeNull();
    }

    [Fact]
    public void GetString_MalformedJson_ReturnsNull()
    {
        var feed = new ExternalFeed { Properties = "{not valid json" };

        ExternalFeedProperties.GetString(feed, "ApiVersion").ShouldBeNull();
    }

    [Fact]
    public void GetString_MissingKey_ReturnsNull()
    {
        var feed = new ExternalFeed { Properties = """{"ApiVersion":"v2"}""" };

        ExternalFeedProperties.GetString(feed, "RegistryPath").ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"DownloadAttempts":"3"}""", 3)]
    [InlineData("""{"Other":"x"}""", 5)]
    public void GetInt_ReturnsValueOrDefault(string json, int expected)
    {
        var feed = new ExternalFeed { Properties = json };

        ExternalFeedProperties.GetInt(feed, "DownloadAttempts", 5).ShouldBe(expected);
    }

    [Theory]
    [InlineData("""{"EnhancedMode":"true"}""", true)]
    [InlineData("""{"Other":"x"}""", false)]
    public void GetBool_ReturnsValueOrDefault(string json, bool expected)
    {
        var feed = new ExternalFeed { Properties = json };

        ExternalFeedProperties.GetBool(feed, "EnhancedMode", false).ShouldBe(expected);
    }

    [Fact]
    public void ParseAll_ValidJson_ReturnsDictionary()
    {
        var feed = new ExternalFeed { Properties = """{"ApiVersion":"v2","RegistryPath":"library"}""" };

        var result = ExternalFeedProperties.ParseAll(feed);

        result.Count.ShouldBe(2);
        result["ApiVersion"].ShouldBe("v2");
        result["RegistryPath"].ShouldBe("library");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseAll_EmptyOrNull_ReturnsEmptyDictionary(string properties)
    {
        var feed = new ExternalFeed { Properties = properties };

        ExternalFeedProperties.ParseAll(feed).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void Serialize_NullOrEmpty_ReturnsNull(int? count)
    {
        var dict = count == null ? null : new Dictionary<string, string>();

        ExternalFeedProperties.Serialize(dict).ShouldBeNull();
    }

    [Fact]
    public void Serialize_ValidDictionary_ReturnsJson()
    {
        var dict = new Dictionary<string, string> { ["ApiVersion"] = "v2", ["RegistryPath"] = "library" };

        var json = ExternalFeedProperties.Serialize(dict);

        json.ShouldNotBeNull();
        json.ShouldContain("\"ApiVersion\":\"v2\"");
        json.ShouldContain("\"RegistryPath\":\"library\"");
    }

    [Fact]
    public void RoundTrip_SerializeThenParse_PreservesValues()
    {
        var original = new Dictionary<string, string>
        {
            ["ApiVersion"] = "v2",
            ["RegistryPath"] = "docker.io",
            ["EnhancedMode"] = "true"
        };

        var json = ExternalFeedProperties.Serialize(original);
        var feed = new ExternalFeed { Properties = json };
        var parsed = ExternalFeedProperties.ParseAll(feed);

        parsed.Count.ShouldBe(3);
        parsed["ApiVersion"].ShouldBe("v2");
        parsed["RegistryPath"].ShouldBe("docker.io");
        parsed["EnhancedMode"].ShouldBe("true");
    }

    [Fact]
    public void GetApiVersion_ReturnsApiVersionProperty()
    {
        var feed = new ExternalFeed { Properties = """{"ApiVersion":"v2"}""" };

        ExternalFeedProperties.GetApiVersion(feed).ShouldBe("v2");
    }

    [Fact]
    public void GetRegistryPath_ReturnsRegistryPathProperty()
    {
        var feed = new ExternalFeed { Properties = """{"RegistryPath":"docker.io"}""" };

        ExternalFeedProperties.GetRegistryPath(feed).ShouldBe("docker.io");
    }
}
