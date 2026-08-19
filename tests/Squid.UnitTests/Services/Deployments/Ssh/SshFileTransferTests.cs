using System.Security.Cryptography;
using System.Text;
using Squid.Core.Services.DeploymentExecution.Ssh;

namespace Squid.UnitTests.Services.Deployments.Ssh;

public class SshFileTransferTests
{
    private static string Sha256Hex(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Theory]
    [InlineData(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F })]
    [InlineData(new byte[] { })]
    public void ComputeLocalSha256_ReturnsCorrectHash(byte[] data)
    {
        SshFileTransfer.ComputeLocalSha256(data).ShouldBe(Sha256Hex(data));
    }

    [Fact]
    public void ComputeLocalSha256_SameDataProducesSameHash()
    {
        var data = Encoding.UTF8.GetBytes("test script content");

        var hash1 = SshFileTransfer.ComputeLocalSha256(data);
        var hash2 = SshFileTransfer.ComputeLocalSha256(data);

        hash1.ShouldBe(hash2);
    }

    [Fact]
    public void ComputeLocalSha256_DifferentDataProducesDifferentHash()
    {
        var data1 = Encoding.UTF8.GetBytes("content A");
        var data2 = Encoding.UTF8.GetBytes("content B");

        SshFileTransfer.ComputeLocalSha256(data1).ShouldNotBe(SshFileTransfer.ComputeLocalSha256(data2));
    }

    [Fact]
    public void ComputeLocalSha256_ReturnsLowercaseHex()
    {
        var data = "hello"u8.ToArray();
        var hash = SshFileTransfer.ComputeLocalSha256(data);

        hash.ShouldBe(Sha256Hex(data));
        hash.ShouldBe(hash.ToLowerInvariant());
        hash.Length.ShouldBe(64);
    }

    [Fact]
    public void GetDirectoryCreationPaths_AbsoluteUnixPath_PreservesLeadingSlash()
    {
        var result = SshFileTransfer.GetDirectoryCreationPaths("/Users/mars/.squid/Work/274");

        result.ShouldBe(new[]
        {
            "/Users",
            "/Users/mars",
            "/Users/mars/.squid",
            "/Users/mars/.squid/Work",
            "/Users/mars/.squid/Work/274"
        });
    }

    [Fact]
    public void GetDirectoryCreationPaths_RelativePath_RemainsRelative()
    {
        var result = SshFileTransfer.GetDirectoryCreationPaths(".squid/Work/274");

        result.ShouldBe(new[]
        {
            ".squid",
            ".squid/Work",
            ".squid/Work/274"
        });
    }

    [Fact]
    public void GetDirectoryCreationPaths_EmptyPath_ReturnsEmpty()
    {
        SshFileTransfer.GetDirectoryCreationPaths(string.Empty).ShouldBeEmpty();
    }
}
