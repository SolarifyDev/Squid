using System.IO;
using System.Linq;
using Squid.Core.Services.Packages.Storage;
using Squid.UnitTests.Services.Packages.Storage.Support;

namespace Squid.UnitTests.Services.Packages.Storage;

public sealed class PackageStoreSettingsTests
{
    private static readonly Squid.Message.Models.PackageStorage.PackageStoreProviderDescriptor Descriptor = new FakeSecretBlobStoreProvider().Describe();
    private static readonly Dictionary<string, string> NoSettings = new();

    private static Dictionary<string, string> Props(string endpoint = "https://x", string mode = null)
    {
        var props = new Dictionary<string, string> { ["Endpoint"] = endpoint };

        if (mode != null) props["Mode"] = mode;

        return props;
    }

    [Fact]
    public void CompleteSettings_AreValid()
    {
        PackageStoreSettings.Validate(Descriptor, Props(mode: "fast"), new Dictionary<string, string> { ["Token"] = "t" }, NoSettings).ShouldBeEmpty();
    }

    [Fact]
    public void MissingRequiredProperty_IsReported_ByLabel()
    {
        var problems = PackageStoreSettings.Validate(Descriptor, new Dictionary<string, string> { ["Endpoint"] = "  " }, new Dictionary<string, string> { ["Token"] = "t" }, NoSettings);

        problems.ShouldBe(["\"Endpoint\" is required"]);
    }

    [Fact]
    public void MissingRequiredSecret_IsReported_UnlessAlreadyStored()
    {
        PackageStoreSettings.Validate(Descriptor, Props(), NoSettings, NoSettings).ShouldBe(["\"Token\" is required"]);
        PackageStoreSettings.Validate(Descriptor, Props(), NoSettings, new Dictionary<string, string> { ["Token"] = "kept" }).ShouldBeEmpty();
    }

    [Fact]
    public void AnEmptySecretDoesNotSatisfyARequiredOne()
    {
        PackageStoreSettings.Validate(Descriptor, Props(), new Dictionary<string, string> { ["Token"] = "" }, NoSettings).ShouldBe(["\"Token\" is required"]);
    }

    [Fact]
    public void UnknownPropertyKey_IsRejected()
    {
        var props = Props();
        props["Colour"] = "red";

        var problems = PackageStoreSettings.Validate(Descriptor, props, new Dictionary<string, string> { ["Token"] = "t" }, NoSettings);

        problems.ShouldHaveSingleItem().ShouldContain("\"Colour\" is not a setting");
    }

    [Fact]
    public void UnknownSecretKey_IsRejected()
    {
        var problems = PackageStoreSettings.Validate(Descriptor, Props(), new Dictionary<string, string> { ["Token"] = "t", ["Nope"] = "x" }, NoSettings);

        problems.ShouldHaveSingleItem().ShouldContain("\"Nope\" is not a secret setting");
    }

    [Fact]
    public void ASecretFieldSentAsAPlainProperty_IsRejected_SoItCanNeverBeStoredUnprotected()
    {
        var props = Props();
        props["Token"] = "leaked-in-properties";

        var problems = PackageStoreSettings.Validate(Descriptor, props, new Dictionary<string, string> { ["Token"] = "t" }, NoSettings);

        problems.ShouldHaveSingleItem().ShouldContain("\"Token\" is not a setting");
    }

    [Fact]
    public void APlainFieldSentAsASecret_IsRejected()
    {
        var problems = PackageStoreSettings.Validate(Descriptor, Props(), new Dictionary<string, string> { ["Token"] = "t", ["Endpoint"] = "x" }, NoSettings);

        problems.ShouldHaveSingleItem().ShouldContain("\"Endpoint\" is not a secret setting");
    }

    [Fact]
    public void SelectValue_MustBeOneOfTheOptions()
    {
        var problems = PackageStoreSettings.Validate(Descriptor, Props(mode: "reckless"), new Dictionary<string, string> { ["Token"] = "t" }, NoSettings);

        problems.ShouldHaveSingleItem().ShouldContain("fast, safe");
    }

    [Fact]
    public void Merge_ProvidedValueReplaces_MissingNullAndEmptyKeepTheStoredOne()
    {
        var stored = new Dictionary<string, string> { ["Token"] = "old-token", ["Proxy"] = "old-proxy" };
        var provided = new Dictionary<string, string> { ["Token"] = "new-token", ["Proxy"] = null };

        PackageStoreSettings.MergeSecrets(stored, provided).ShouldBe(new Dictionary<string, string> { ["Token"] = "new-token", ["Proxy"] = "old-proxy" });
        PackageStoreSettings.MergeSecrets(stored, new Dictionary<string, string> { ["Token"] = "" }).ShouldBe(stored);
        PackageStoreSettings.MergeSecrets(stored, NoSettings).ShouldBe(stored);
    }

    [Fact]
    public void Merge_AddsASecretThatWasNotStoredYet_AndDoesNotMutateItsInputs()
    {
        var stored = new Dictionary<string, string> { ["Token"] = "t" };

        var merged = PackageStoreSettings.MergeSecrets(stored, new Dictionary<string, string> { ["Proxy"] = "p" });

        merged.ShouldBe(new Dictionary<string, string> { ["Token"] = "t", ["Proxy"] = "p" });
        stored.Count.ShouldBe(1);
    }
}
