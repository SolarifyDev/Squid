using System.IO;
using System.Net.Sockets;
using Renci.SshNet.Common;
using Squid.Core.Services.DeploymentExecution.Ssh;

namespace Squid.UnitTests.Services.Deployments.Ssh;

public class SshTransientErrorDetectorTests
{
    [Fact]
    public void IsTransient_SshConnectionException_True()
    {
        SshTransientErrorDetector.IsTransient(new SshConnectionException("Connection lost")).ShouldBeTrue();
    }

    [Fact]
    public void IsTransient_SocketException_True()
    {
        SshTransientErrorDetector.IsTransient(new SocketException()).ShouldBeTrue();
    }

    [Fact]
    public void IsTransient_IOException_True()
    {
        SshTransientErrorDetector.IsTransient(new IOException("Network stream error")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Client not connected")]
    [InlineData("Channel was closed")]
    [InlineData("An existing connection was forcibly closed by the remote host")]
    [InlineData("connected party did not properly respond after a period of time")]
    public void IsTransient_TransientMessage_True(string message)
    {
        SshTransientErrorDetector.IsTransient(new InvalidOperationException(message)).ShouldBeTrue();
    }

    [Fact]
    public void IsTransient_SshAuthenticationException_False()
    {
        SshTransientErrorDetector.IsTransient(new SshAuthenticationException("Bad key")).ShouldBeFalse();
    }

    [Fact]
    public void IsTransient_SftpPermissionDeniedException_False()
    {
        SshTransientErrorDetector.IsTransient(new SftpPermissionDeniedException("Permission denied")).ShouldBeFalse();
    }

    [Fact]
    public void IsTransient_ArgumentException_False()
    {
        SshTransientErrorDetector.IsTransient(new ArgumentException("Invalid argument")).ShouldBeFalse();
    }

    [Fact]
    public void IsTransient_GenericException_NoTransientMessage_False()
    {
        SshTransientErrorDetector.IsTransient(new InvalidOperationException("Something else")).ShouldBeFalse();
    }
}
