using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

public class OctopusRestPathPolicyTests
{
    // Every request the Octopus source sends today or the whole-space fetch will send, as it is written on the wire.
    [Theory]
    [InlineData("api")]
    [InlineData("/api")]
    [InlineData("api/users/me")]
    [InlineData("api/users/Users-7/permissions?spaces=Spaces-1&includeSystem=true")]
    [InlineData("api/spaces/all")]
    [InlineData("api/teams/all?spaces=Spaces-1&includeSystem=true")]
    [InlineData("api/tasks?ids=ServerTasks-1,ServerTasks-2&skip=0&take=2&spaces=Spaces-1")]
    [InlineData("api/Spaces-1/tasks/ServerTasks-1/details?verbose=false&tail=1000")]
    [InlineData("api/Spaces-1/projects/Projects-1/releases?skip=100&take=100")]
    [InlineData("api/Spaces-1/projects/Projects-1/deploymentsettings")]
    [InlineData("api/Spaces-1/projects/Projects-1/channels")]
    [InlineData("api/Spaces-1/deployments?projects=Projects-1&skip=0&take=100")]
    [InlineData("api/Spaces-1/deploymentprocesses/deploymentprocess-Projects-1-s-4-ABCDE")]
    [InlineData("api/Spaces-1/variables/variableset-Projects-1")]
    [InlineData("api/Spaces-1/interruptions?regarding=ServerTasks-1&skip=0&take=100")]
    [InlineData("api/Spaces-1/events?regarding=Deployments-1&documentTypes=Deployments,ServerTasks,Interruptions")]
    [InlineData("api/Spaces-1/artifacts?regarding=ServerTasks-1")]
    [InlineData("api/Spaces-1/machines/all")]
    [InlineData("api/Spaces-1/feeds?skip=0&take=100")]
    [InlineData("api/Spaces-1/projects?ids=Projects-1,Projects-2")]
    [InlineData("api/Spaces-12/certificates/all")]
    [InlineData("api/projects/Projects-1/releases?skip=0&take=100")]
    [InlineData("API/SPACES-1/PROJECTS/all")]
    public void Allowed(string path)
    {
        OctopusRestPathPolicy.IsAllowed(path).ShouldBeTrue();
    }

    // GET is not harmless on Octopus: API keys, exports, package search, discovery that dials a host, arbitrary paths.
    [Theory]
    [InlineData("api/users/Users-1/apikeys")]
    [InlineData("api/users/me/apikeys")]
    [InlineData("api/users/all")]
    [InlineData("api/Spaces-1/packages/search?term=x")]
    [InlineData("api/Spaces-1/machines/discover?host=10.0.0.1&port=10933")]
    [InlineData("api/Spaces-1/machines/Machines-1")]
    [InlineData("api/Spaces-1/certificates/Certificates-1/export")]
    [InlineData("api/Spaces-1/projects/Projects-1/export")]
    [InlineData("api/Spaces-1/tasks/ServerTasks-1/raw")]
    [InlineData("api/Spaces-1/projects/../users/me")]
    [InlineData("api/Spaces-1/variables/..")]
    [InlineData("api/Spaces-1/variables/.")]
    [InlineData("api/Spaces-1/variables/a%2Fb")]
    [InlineData("api/Spaces-1/projects/Projects 1/releases")]
    [InlineData("api/Spaces-1/interruptions?regarding=ServerTasks-1%26x")]
    [InlineData("api/Spaces-1/interruptions?regarding=")]
    [InlineData("api/Spaces-1/interruptions?regarding")]
    [InlineData("api/Spaces-1/interruptions?re-garding=ServerTasks-1")]
    [InlineData("api/Spaces-1/interruptions?regarding=ServerTasks-1&")]
    [InlineData("api//projects/all")]
    [InlineData("api/")]
    [InlineData("api/Spaces-1/Spaces-2/projects/all")]
    [InlineData("octopus/api")]
    [InlineData("https://evil.example.net/api")]
    [InlineData("")]
    [InlineData(null)]
    public void Denied(string path)
    {
        OctopusRestPathPolicy.IsAllowed(path).ShouldBeFalse();
        Should.Throw<OctopusRestException>(() => OctopusRestPathPolicy.EnsureAllowed(path)).Kind.ShouldBe(OctopusRestErrorKind.Denied);
    }

    [Theory]
    [InlineData("api", true)]
    [InlineData("api/users/me", true)]
    [InlineData("/API/Users/Me", true)]
    [InlineData("api/users/Users-7/permissions", false)]
    [InlineData("api/spaces/all", false)]
    [InlineData("api/Spaces-1/projects/Projects-1/releases?skip=0&take=100", false)]
    public void IsIdentityPath(string path, bool expected)
    {
        OctopusRestPathPolicy.IsIdentityPath(path).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Projects-1", true)]
    [InlineData("deploymentprocess-Projects-1-s-4-ABCDE", true)]
    [InlineData("8a6f2b3c-0000-4000-8000-000000000001", true)]
    [InlineData("feeds-docker-hub", true)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("a/b", false)]
    [InlineData("a%2Fb", false)]
    public void IsValidId(string id, bool expected)
    {
        OctopusRestPathPolicy.IsValidId(id).ShouldBe(expected);
    }

    [Fact]
    public void IdPattern_Pinned()
    {
        OctopusRestPathPolicy.IdPattern.ShouldBe("^[A-Za-z0-9._-]+$");
    }

    // Single items are never addressed by path (a closure fetch uses {kind}?ids=), so "machines/discover" cannot look like an item.
    [Fact]
    public void Templates_NeverAddressACollectionItemByPath()
    {
        OctopusRestPathPolicy.Templates.ShouldNotContain(t => OctopusRestPathPolicy.Collections.Any(c => t == c + "/{id}"));
    }
}
