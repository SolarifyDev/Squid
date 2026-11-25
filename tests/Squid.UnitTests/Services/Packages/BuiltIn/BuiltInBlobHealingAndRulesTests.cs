using System.Globalization;
using System.Linq;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Validators.BuiltInPackages;
using Squid.Message.Requests.BuiltInPackages;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

[Collection("BuiltInStaging")]
public class BuiltInBlobHealingAndRulesTests
{
    private readonly PushHarness _h = new();

    private static readonly byte[] Bytes = TestArchives.Zip(("index.html", "hello"));

    private string KeyOf(byte[] bytes) => BuiltInBlobKey.For(TestArchives.Sha256Of(bytes), ".zip");

    [Fact]
    public async Task Push_WhenTheBlobIsTruncated_WritesItAgain()
    {
        await _h.Service().PushAsync(PushHarness.Push(Bytes, version: "1.0.0"), CancellationToken.None);

        _h.Blobs.Blobs[KeyOf(Bytes)] = Bytes[..(Bytes.Length / 2)];

        await _h.Service().PushAsync(PushHarness.Push(Bytes, version: "2.0.0"), CancellationToken.None);

        _h.Blobs.Blobs[KeyOf(Bytes)].ShouldBe(Bytes);
        _h.Blobs.Puts.ShouldBe(2);
    }

    [Fact]
    public async Task Push_WhenTheBlobHasTheRightSizeButOtherBytes_WritesItAgain()
    {
        await _h.Service().PushAsync(PushHarness.Push(Bytes, version: "1.0.0"), CancellationToken.None);

        var corrupt = (byte[])Bytes.Clone();

        corrupt[^5] ^= 0xFF;
        _h.Blobs.Blobs[KeyOf(Bytes)] = corrupt;

        await _h.Service().PushAsync(PushHarness.Push(Bytes, version: "2.0.0"), CancellationToken.None);

        _h.Blobs.Blobs[KeyOf(Bytes)].ShouldBe(Bytes);
    }

    [Fact]
    public async Task Push_WhenTheBlobIsIntact_IsNotWrittenAgain()
    {
        await _h.Service().PushAsync(PushHarness.Push(Bytes, version: "1.0.0"), CancellationToken.None);
        await _h.Service().PushAsync(PushHarness.Push(Bytes, version: "2.0.0"), CancellationToken.None);

        _h.Blobs.Puts.ShouldBe(1);
    }

    [Fact]
    public void ListRequest_Take_IsBoundedTo1Through500()
    {
        var validator = new ListBuiltInPackagesRequestValidator();

        foreach (var ok in new[] { 1, 30, 500 }) validator.Validate(new ListBuiltInPackagesRequest { Take = ok }).IsValid.ShouldBeTrue();
        foreach (var bad in new[] { 0, -1, 501, int.MaxValue }) validator.Validate(new ListBuiltInPackagesRequest { Take = bad }).IsValid.ShouldBeFalse();

        ListBuiltInPackagesRequestValidator.MaxTake.ShouldBe(500);
    }

    [Theory]
    [InlineData("İstanbul.App")]
    [InlineData("Ünï.Code")]
    [InlineData("日本.pkg")]
    public void Ids_AreAsciiOnly(string id)
    {
        Should.Throw<InvalidPackageIdException>(() => BuiltInPackageIds.Resolve(id, null));
    }

    [Fact]
    public async Task Push_UnderTheTurkishCulture_FindsTheSamePackageWhateverTheCasing()
    {
        var previous = CultureInfo.CurrentCulture;

        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

        try
        {
            var first = await _h.Service().PushAsync(PushHarness.Push(TestArchives.Zip(("a.txt", "1")), packageId: "TITLE.Index"), CancellationToken.None);
            var second = await _h.Service().PushAsync(PushHarness.Push(TestArchives.Zip(("a.txt", "2")), packageId: "title.index"), CancellationToken.None);

            second.Package.PackageId.ShouldBe(first.Package.PackageId);
            _h.Packages.Rows.Select(r => r.PackageId).Distinct().Count().ShouldBe(1);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
