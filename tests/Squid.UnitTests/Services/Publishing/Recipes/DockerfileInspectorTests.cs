using Squid.Core.Services.Publishing;
using Squid.Message.Models.Publishing;

namespace Squid.UnitTests.Services.Publishing.Recipes;

public class DockerfileInspectorTests
{
    private static async Task<string> ExposedAsync(string dockerfile, string name = "Dockerfile")
    {
        var facts = await Inspect.ZipAsync((name, dockerfile), ("index.js", "x"));

        return facts.Get(PackageFactKeys.DockerfileExposePort);
    }

    [Theory]
    [InlineData("FROM node\nEXPOSE 8080\n", "8080")]
    [InlineData("FROM node\nEXPOSE 8080/tcp\n", "8080")]
    [InlineData("FROM node\nexpose 3000\n", "3000")]
    [InlineData("FROM node\n  EXPOSE   80   443\n", "80")]
    [InlineData("FROM node\r\nEXPOSE 9000\r\nCMD [\"x\"]\r\n", "9000")]
    [InlineData("FROM node\nEXPOSE 53/udp 8081/tcp\n", "8081")]
    [InlineData("FROM node\nEXPOSE 8000-8010\n", "8000")]
    [InlineData("FROM node\nEXPOSE 7000 # web\n", "7000")]
    [InlineData("FROM node\nEXPOSE 1111\nEXPOSE 2222\n", "1111")]
    public async Task FirstTcpPort_IsRecorded(string dockerfile, string expected)
    {
        (await ExposedAsync(dockerfile)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("FROM node\nCMD [\"x\"]\n")]
    [InlineData("FROM node\n# EXPOSE 8080\n")]
    [InlineData("FROM node\nEXPOSE $PORT\n")]
    [InlineData("FROM node\nEXPOSE 53/udp\n")]
    [InlineData("FROM node\nEXPOSE 99999\n")]
    [InlineData("")]
    public async Task NoUsablePort_RecordsNothing(string dockerfile)
    {
        (await ExposedAsync(dockerfile)).ShouldBeNull();
    }

    [Fact]
    public async Task NoDockerfile_RecordsNothing()
    {
        (await ExposedAsync("EXPOSE 8080", name: "notes.txt")).ShouldBeNull();
    }

    [Fact]
    public async Task DockerfileInAWrappedArchive_IsFoundAtTheEffectiveRoot()
    {
        var facts = await Inspect.ZipAsync(("app/Dockerfile", "FROM node\nEXPOSE 4000\n"), ("app/index.js", "x"));

        facts.Get(PackageFactKeys.DockerfileExposePort).ShouldBe("4000");
        facts.Is(PackageFactKeys.MarkerDockerfile).ShouldBeTrue();
    }

    [Fact]
    public void FactKey_IsPinned()
    {
        PackageFactKeys.DockerfileExposePort.ShouldBe("dockerfile.expose.port");
    }
}
