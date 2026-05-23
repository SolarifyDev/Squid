using System.Text.Json;
using Shouldly;
using Squid.Core.Services.DeploymentExecution.Tentacle;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// H2 — pin the persisted JSON shape so future refactors of
/// <see cref="MachineRuntimeCapabilities"/> (adding properties, renaming) don't
/// silently break the hydrate round-trip on operator's existing data. The DB
/// rows persist across server upgrades; a schema change here without a
/// migration would mean H2's hydrator can no longer parse rows written by an
/// older server.
/// </summary>
public sealed class MachineRuntimeCapabilitiesPersistenceTests
{
    [Fact]
    public void SerialisedJsonShape_Stable_AcrossKnownProperties()
    {
        // The exact JSON shape consumed by hydrator. If property names change
        // (case, spelling), pre-existing rows from older servers will fail to
        // deserialise → the H1 cold-cache UX returns until the next health
        // check overwrites each row. Pin the shape.
        var persisted = new MachineRuntimeCapabilitiesPersistence.PersistedCapabilities
        {
            Os = "Windows",
            OsVersion = "10.0.19045.0",
            DefaultShell = "powershell",
            InstalledShells = "powershell,cmd",
            Architecture = "X64",
            AgentVersion = "1.7.9",
            SupportedServices = new[] { "IScriptService/v1" }
        };

        var json = JsonSerializer.Serialize(persisted, MachineRuntimeCapabilitiesPersistence.SerializerOptions);

        // CamelCase per the convention set in SerializerOptions.
        json.ShouldContain("\"os\":\"Windows\"");
        json.ShouldContain("\"osVersion\":\"10.0.19045.0\"");
        json.ShouldContain("\"defaultShell\":\"powershell\"");
        json.ShouldContain("\"installedShells\":\"powershell,cmd\"");
        json.ShouldContain("\"architecture\":\"X64\"");
        json.ShouldContain("\"agentVersion\":\"1.7.9\"");
        json.ShouldContain("\"supportedServices\":[\"IScriptService/v1\"]");
    }

    [Fact]
    public void Deserialise_FromCanonicalJsonShape_RoundTripsCorrectly()
    {
        // Inverse of the serialisation pin: an operator's existing row
        // (written by 1.8.0 server) MUST deserialise correctly even after the
        // SerializerOptions defaults change. Hard-coding the JSON literal here
        // means a .NET 10 PascalCase regression or a JsonNamingPolicy refactor
        // is a test-time-visible decision.
        var json = """
            {
              "os": "Linux",
              "osVersion": "6.5.0",
              "defaultShell": "bash",
              "installedShells": "bash,zsh",
              "architecture": "Arm64",
              "agentVersion": "1.8.0",
              "supportedServices": ["IScriptService/v1", "IScriptService/v2"]
            }
            """;

        var persisted = JsonSerializer.Deserialize<MachineRuntimeCapabilitiesPersistence.PersistedCapabilities>(
            json, MachineRuntimeCapabilitiesPersistence.SerializerOptions);

        persisted.ShouldNotBeNull();
        persisted!.Os.ShouldBe("Linux");
        persisted.OsVersion.ShouldBe("6.5.0");
        persisted.DefaultShell.ShouldBe("bash");
        persisted.InstalledShells.ShouldBe("bash,zsh");
        persisted.Architecture.ShouldBe("Arm64");
        persisted.AgentVersion.ShouldBe("1.8.0");
        persisted.SupportedServices.ShouldBe(new[] { "IScriptService/v1", "IScriptService/v2" });
    }

    [Fact]
    public void Deserialise_MissingOptionalFields_DoesNotThrow_ProducesNulls()
    {
        // Older rows (or partial fixtures) may not have every field. The DTO
        // marks all properties nullable specifically for this — deserialising
        // a row with only `os` must succeed, and the hydrator's coalesce-to-
        // empty-string logic handles the rest.
        var json = """{"os":"Windows","agentVersion":"1.6.5"}""";

        var persisted = JsonSerializer.Deserialize<MachineRuntimeCapabilitiesPersistence.PersistedCapabilities>(
            json, MachineRuntimeCapabilitiesPersistence.SerializerOptions);

        persisted.ShouldNotBeNull();
        persisted!.Os.ShouldBe("Windows");
        persisted.AgentVersion.ShouldBe("1.6.5");
        persisted.OsVersion.ShouldBeNull();
        persisted.DefaultShell.ShouldBeNull();
        persisted.SupportedServices.ShouldBeNull();
    }

    [Fact]
    public void SerializerOptions_NamingPolicy_PinnedToCamelCase()
    {
        // Drift detector: future PRs MUST NOT flip this to PascalCase or
        // SnakeCase — every existing row in production would become un-readable
        // for the hydrator. The choice is wire contract; pin it.
        MachineRuntimeCapabilitiesPersistence.SerializerOptions.PropertyNamingPolicy
            .ShouldBe(JsonNamingPolicy.CamelCase);
    }

    [Fact]
    public void SerializerOptions_WriteIndented_PinnedToFalse()
    {
        // Tiny optimisation: jsonb storage is more efficient without leading
        // whitespace, and the operator's diff tools handle it fine. Pin so a
        // "make logs prettier" PR doesn't accidentally bloat DB rows.
        MachineRuntimeCapabilitiesPersistence.SerializerOptions.WriteIndented.ShouldBeFalse();
    }
}
