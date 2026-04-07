using Squid.Core.Services.DeploymentExecution.Ssh;

namespace Squid.UnitTests.Services.Deployments.Ssh;

public class SshFileTransferTests
{
    [Theory]
    [InlineData(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }, "8b1a9953c4611296a827abf8c47804d7")]
    [InlineData(new byte[] { }, "d41d8cd98f00b204e9800998ecf8427e")]
    public void ComputeLocalMd5_ReturnsCorrectHash(byte[] data, string expectedHash)
    {
        SshFileTransfer.ComputeLocalMd5(data).ShouldBe(expectedHash);
    }

    [Fact]
    public void ComputeLocalMd5_SameDataProducesSameHash()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("test script content");

        var hash1 = SshFileTransfer.ComputeLocalMd5(data);
        var hash2 = SshFileTransfer.ComputeLocalMd5(data);

        hash1.ShouldBe(hash2);
    }

    [Fact]
    public void ComputeLocalMd5_DifferentDataProducesDifferentHash()
    {
        var data1 = System.Text.Encoding.UTF8.GetBytes("content A");
        var data2 = System.Text.Encoding.UTF8.GetBytes("content B");

        SshFileTransfer.ComputeLocalMd5(data1).ShouldNotBe(SshFileTransfer.ComputeLocalMd5(data2));
    }

    [Fact]
    public void ComputeLocalMd5_ReturnsLowercaseHex()
    {
        var hash = SshFileTransfer.ComputeLocalMd5(new byte[] { 0xFF });

        hash.ShouldBe(hash.ToLowerInvariant());
        hash.Length.ShouldBe(32);
    }
}
