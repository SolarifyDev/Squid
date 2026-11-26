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

    private static StoredSecrets Reusable(Dictionary<string, string> secrets) => new(secrets, new Dictionary<string, string>());

    private static StoredSecrets Stored(params (string Key, string Value)[] secrets) => Reusable(secrets.ToDictionary(s => s.Key, s => s.Value));

    [Fact]
    public void CompleteSettings_AreValid()
    {
        PackageStoreSettings.Validate(Descriptor, Props(mode: "fast"), new Dictionary<string, string> { ["Token"] = "t" }, StoredSecrets.None).ShouldBeEmpty();
    }

    [Fact]
    public void MissingRequiredProperty_IsReported_ByLabel()
    {
        var problems = PackageStoreSettings.Validate(Descriptor, new Dictionary<string, string> { ["Endpoint"] = "  " }, new Dictionary<string, string> { ["Token"] = "t" }, StoredSecrets.None);

        problems.ShouldBe(["\"Endpoint\" is required"]);
    }

    [Fact]
    public void MissingRequiredSecret_IsReported_UnlessAlreadyStored()
    {
        PackageStoreSettings.Validate(Descriptor, Props(), NoSettings, StoredSecrets.None).ShouldBe(["\"Token\" is required"]);
        PackageStoreSettings.Validate(Descriptor, Props(), NoSettings, Stored(("Token", "kept"))).ShouldBeEmpty();
    }

    [Fact]
    public void AnEmptySecretDoesNotSatisfyARequiredOne()
    {
        PackageStoreSettings.Validate(Descriptor, Props(), new Dictionary<string, string> { ["Token"] = "" }, StoredSecrets.None).ShouldBe(["\"Token\" is required"]);
    }

    [Fact]
    public void UnknownPropertyKey_IsRejected()
    {
        var props = Props();
        props["Colour"] = "red";

        var problems = PackageStoreSettings.Validate(Descriptor, props, new Dictionary<string, string> { ["Token"] = "t" }, StoredSecrets.None);

        problems.ShouldHaveSingleItem().ShouldContain("\"Colour\" is not a setting");
    }

    [Fact]
    public void UnknownSecretKey_IsRejected()
    {
        var problems = PackageStoreSettings.Validate(Descriptor, Props(), new Dictionary<string, string> { ["Token"] = "t", ["Nope"] = "x" }, StoredSecrets.None);

        problems.ShouldHaveSingleItem().ShouldContain("\"Nope\" is not a secret setting");
    }

    [Fact]
    public void ASecretFieldSentAsAPlainProperty_IsRejected_SoItCanNeverBeStoredUnprotected()
    {
        var props = Props();
        props["Token"] = "leaked-in-properties";

        var problems = PackageStoreSettings.Validate(Descriptor, props, new Dictionary<string, string> { ["Token"] = "t" }, StoredSecrets.None);

        problems.ShouldHaveSingleItem().ShouldContain("\"Token\" is not a setting");
    }

    [Fact]
    public void APlainFieldSentAsASecret_IsRejected()
    {
        var problems = PackageStoreSettings.Validate(Descriptor, Props(), new Dictionary<string, string> { ["Token"] = "t", ["Endpoint"] = "x" }, StoredSecrets.None);

        problems.ShouldHaveSingleItem().ShouldContain("\"Endpoint\" is not a secret setting");
    }

    [Fact]
    public void SelectValue_MustBeOneOfTheOptions()
    {
        var problems = PackageStoreSettings.Validate(Descriptor, Props(mode: "reckless"), new Dictionary<string, string> { ["Token"] = "t" }, StoredSecrets.None);

        problems.ShouldHaveSingleItem().ShouldContain("fast, safe");
    }

    [Fact]
    public void Merge_ProvidedValueReplaces_MissingNullAndEmptyKeepTheStoredOne()
    {
        var stored = new Dictionary<string, string> { ["Token"] = "old-token", ["Proxy"] = "old-proxy" };
        var provided = new Dictionary<string, string> { ["Token"] = "new-token", ["Proxy"] = null };

        PackageStoreSettings.MergeSecrets(Reusable(stored), provided).ShouldBe(new Dictionary<string, string> { ["Token"] = "new-token", ["Proxy"] = "old-proxy" });
        PackageStoreSettings.MergeSecrets(Reusable(stored), new Dictionary<string, string> { ["Token"] = "" }).ShouldBe(stored);
        PackageStoreSettings.MergeSecrets(Reusable(stored), NoSettings).ShouldBe(stored);
    }

    [Fact]
    public void Merge_AddsASecretThatWasNotStoredYet_AndDoesNotMutateItsInputs()
    {
        var stored = new Dictionary<string, string> { ["Token"] = "t" };

        var merged = PackageStoreSettings.MergeSecrets(Reusable(stored), new Dictionary<string, string> { ["Proxy"] = "p" });

        merged.ShouldBe(new Dictionary<string, string> { ["Token"] = "t", ["Proxy"] = "p" });
        stored.Count.ShouldBe(1);
    }

    private static readonly Dictionary<string, string> StoredProps = new() { ["Endpoint"] = "https://stored", ["Mode"] = "fast" };
    private static readonly Dictionary<string, string> StoredToken = new() { ["Token"] = "stored-token", ["Proxy"] = "stored-proxy" };

    [Fact]
    public void SelectReusable_KeepsEverything_WhileTheBoundFieldsAreUnchanged()
    {
        var stored = PackageStoreSettings.SelectReusable(Descriptor, new Dictionary<string, string>(StoredProps), StoredProps, StoredToken);

        stored.Reusable.ShouldBe(StoredToken);
        stored.Withheld.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Endpoint", "https://attacker", "Endpoint")]
    [InlineData("Mode", "safe", "Mode")]
    [InlineData("Endpoint", "", "Endpoint")]
    public void SelectReusable_WithholdsABoundSecret_WhenABoundFieldChanged_ButKeepsTheUnboundOne(string field, string newValue, string expectedLabel)
    {
        var edited = new Dictionary<string, string>(StoredProps) { [field] = newValue };

        var stored = PackageStoreSettings.SelectReusable(Descriptor, edited, StoredProps, StoredToken);

        stored.Reusable.ShouldBe(new Dictionary<string, string> { ["Proxy"] = "stored-proxy" }, "Proxy is not bound to anything");
        stored.Withheld.ShouldBe(new Dictionary<string, string> { ["Token"] = expectedLabel });
    }

    [Fact]
    public void SelectReusable_TreatsAMissingBoundFieldAndAnEmptyOneAsTheSame()
    {
        var storedWithoutMode = new Dictionary<string, string> { ["Endpoint"] = "https://stored" };
        var edited = new Dictionary<string, string> { ["Endpoint"] = "https://stored", ["Mode"] = "" };

        PackageStoreSettings.SelectReusable(Descriptor, edited, storedWithoutMode, StoredToken).Withheld.ShouldBeEmpty();
    }

    [Fact]
    public void ARequiredSecretThatWasWithheld_IsReportedAsMustBeEnteredAgain_ByTheChangedFieldsLabel()
    {
        var edited = new Dictionary<string, string>(StoredProps) { ["Endpoint"] = "https://attacker" };
        var stored = PackageStoreSettings.SelectReusable(Descriptor, edited, StoredProps, StoredToken);

        var problems = PackageStoreSettings.Validate(Descriptor, edited, NoSettings, stored);

        problems.ShouldBe(["\"Token\" must be entered again because \"Endpoint\" was changed"]);
        PackageStoreSettings.Validate(Descriptor, edited, new Dictionary<string, string> { ["Token"] = "retyped" }, stored).ShouldBeEmpty();
    }

    [Fact]
    public void EveryBoundToKey_OfEveryProvidersDescriptor_NamesARealNonSecretField()
    {
        foreach (var field in Descriptor.Fields.Where(f => f.BoundTo.Count > 0))
        {
            field.Kind.ShouldBe(Squid.Message.Enums.PackageStorage.PackageStoreFieldKind.Secret);
            field.BoundTo.ShouldAllBe(key => Descriptor.Fields.Any(f => f.Key == key && f.Kind != Squid.Message.Enums.PackageStorage.PackageStoreFieldKind.Secret));
        }
    }
}
