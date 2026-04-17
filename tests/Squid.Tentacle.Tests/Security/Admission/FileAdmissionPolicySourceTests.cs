using Shouldly;
using Squid.Tentacle.Security.Admission;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Security.Admission;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class FileAdmissionPolicySourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"squid-policy-test-{Guid.NewGuid():N}");

    public FileAdmissionPolicySourceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void MissingFile_StartsWithEmptyPolicy()
    {
        using var source = new FileAdmissionPolicySource(Path.Combine(_dir, "missing.yaml"), ParseJson);

        source.Current.Rules.ShouldBeEmpty();
    }

    [Fact]
    public void ExistingFile_ParsesOnConstruction()
    {
        var path = Path.Combine(_dir, "policy.json");
        File.WriteAllText(path, """
            {
              "version": 1,
              "rules": [{ "id": "initial", "denyScriptBodyRegex": ["rm -rf"] }]
            }
            """);

        using var source = new FileAdmissionPolicySource(path, ParseJson);

        source.Current.Rules.ShouldHaveSingleItem();
        source.Current.Rules[0].Id.ShouldBe("initial");
    }

    [Fact]
    public async Task HotReload_FileRewritten_UpdatesPolicy()
    {
        var path = Path.Combine(_dir, "policy.json");
        File.WriteAllText(path, """{"rules":[{"id":"v1","denyScriptBodyRegex":["a"]}]}""");

        using var source = new FileAdmissionPolicySource(path, ParseJson);
        source.Current.Rules.ShouldHaveSingleItem();
        source.Current.Rules[0].Id.ShouldBe("v1");

        var updates = new List<AdmissionPolicy>();
        source.Updated += p => updates.Add(p);

        File.WriteAllText(path, """{"rules":[{"id":"v2","denyScriptBodyRegex":["b"]},{"id":"v2b"}]}""");

        await WaitForCondition(() => source.Current.Rules.Count == 2 && source.Current.Rules[0].Id == "v2", TimeSpan.FromSeconds(3));

        source.Current.Rules.Count.ShouldBe(2);
        source.Current.Rules[0].Id.ShouldBe("v2");
    }

    [Fact]
    public async Task MalformedUpdate_KeepsLastGoodPolicy()
    {
        var path = Path.Combine(_dir, "policy.json");
        File.WriteAllText(path, """{"rules":[{"id":"good","denyScriptBodyRegex":["x"]}]}""");

        using var source = new FileAdmissionPolicySource(path, ParseJson);
        source.Current.Rules[0].Id.ShouldBe("good");

        File.WriteAllText(path, "{ not-json");
        await Task.Delay(300);   // allow watcher to fire

        source.Current.Rules[0].Id.ShouldBe("good", "malformed policy must be rejected in favour of the last-known-good copy");
    }

    private static AdmissionPolicy ParseJson(string content)
    {
        return System.Text.Json.JsonSerializer.Deserialize<AdmissionPolicy>(content, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? AdmissionPolicy.Empty();
    }

    private static async Task WaitForCondition(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow > deadline) return;
            await Task.Delay(50);
        }
    }
}
