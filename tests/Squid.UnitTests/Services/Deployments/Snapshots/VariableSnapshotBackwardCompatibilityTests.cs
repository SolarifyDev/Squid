using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.Common;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Snapshots;

public class VariableSnapshotBackwardCompatibilityTests
{
    [Theory]
    [InlineData("""{"Variables":[{"Id":1,"Name":"Env","LastModifiedBy":null}]}""", null)]
    [InlineData("""{"Variables":[{"Id":1,"Name":"Env","LastModifiedBy":"System"}]}""", null)]
    [InlineData("""{"Variables":[{"Id":1,"Name":"Env","LastModifiedBy":"42"}]}""", 42)]
    [InlineData("""{"Variables":[{"Id":1,"Name":"Env","LastModifiedBy":7}]}""", 7)]
    [InlineData("""{"Variables":[{"Id":1,"Name":"Env"}]}""", null)]
    public void Deserialize_OldSnapshotJson_HandlesLastModifiedBy(string json, int? expectedLastModifiedBy)
    {
        var snapshot = JsonSerializer.Deserialize<VariableSetSnapshotDataDto>(json);

        snapshot.ShouldNotBeNull();
        snapshot.Variables.Count.ShouldBe(1);
        snapshot.Variables[0].LastModifiedBy.ShouldBe(expectedLastModifiedBy);
    }

    [Fact]
    public void Deserialize_CompressedOldSnapshot_HandlesLegacyFields()
    {
        var oldJson = """{"Variables":[{"Id":1,"VariableSetId":10,"Name":"DbHost","Value":"localhost","LastModifiedOn":"2026-01-15T00:00:00Z","LastModifiedBy":null}]}""";
        var compressed = CompressRawJson(oldJson);

        var snapshot = UtilService.DecompressFromGzip<VariableSetSnapshotDataDto>(compressed);

        snapshot.ShouldNotBeNull();
        snapshot.Variables.Count.ShouldBe(1);
        snapshot.Variables[0].Name.ShouldBe("DbHost");
        snapshot.Variables[0].LastModifiedBy.ShouldBeNull();
    }

    [Fact]
    public void RoundTrip_NewSnapshot_PreservesLastModifiedBy()
    {
        var data = new VariableSetSnapshotDataDto
        {
            Variables = new List<VariableDto>
            {
                new() { Id = 1, Name = "Env", LastModifiedBy = 42 }
            }
        };

        var json = JsonSerializer.Serialize(data);
        var deserialized = JsonSerializer.Deserialize<VariableSetSnapshotDataDto>(json);

        deserialized.Variables[0].LastModifiedBy.ShouldBe(42);
    }

    private static byte[] CompressRawJson(string json)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Optimal))
        using (var sw = new StreamWriter(gzip, Encoding.UTF8))
        {
            sw.Write(json);
        }

        return ms.ToArray();
    }
}
