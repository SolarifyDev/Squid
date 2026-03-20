using Mediator.Net.Context;
using Mediator.Net.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Squid.Core.Middlewares.SpaceScope;
using Squid.Message.Contracts;

namespace Squid.UnitTests.Middlewares;

public class SpaceIdInjectionSpecificationTests
{
    [Fact]
    public async Task InjectsSpaceId_WhenHeaderPresent_AndPropertyNull()
    {
        var spec = CreateSpec("5");
        var message = new TestDirectSpaceScopedRequest();
        var context = CreateContext(message);

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        message.SpaceId.ShouldBe(5);
    }

    [Fact]
    public async Task SkipsInjection_WhenSpaceIdAlreadySet()
    {
        var spec = CreateSpec("5");
        var message = new TestDirectSpaceScopedRequest { SpaceId = 3 };
        var context = CreateContext(message);

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        message.SpaceId.ShouldBe(3);
    }

    [Fact]
    public async Task SkipsInjection_WhenHeaderMissing()
    {
        var spec = CreateSpec(null);
        var message = new TestDirectSpaceScopedRequest();
        var context = CreateContext(message);

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        message.SpaceId.ShouldBeNull();
    }

    [Fact]
    public async Task SkipsInjection_WhenHeaderInvalid()
    {
        var spec = CreateSpec("abc");
        var message = new TestDirectSpaceScopedRequest();
        var context = CreateContext(message);

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        message.SpaceId.ShouldBeNull();
    }

    [Fact]
    public async Task SkipsInjection_WhenExplicitInterfaceImpl()
    {
        var spec = CreateSpec("5");
        var message = new TestExplicitSpaceScopedCommand();
        var context = CreateContext(message);

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        ((ISpaceScoped)message).SpaceId.ShouldBe(99);
    }

    [Fact]
    public async Task SkipsInjection_WhenNotSpaceScoped()
    {
        var spec = CreateSpec("5");
        var message = new TestNonSpaceScopedRequest();
        var context = CreateContext(message);

        await spec.BeforeExecute(context.Object, CancellationToken.None);
    }

    [Fact]
    public async Task SkipsInjection_WhenNonNullableSpaceId()
    {
        var spec = CreateSpec("5");
        var message = new TestNonNullableSpaceScopedRequest();
        var context = CreateContext(message);

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        message.SpaceId.ShouldBe(0);
    }

    // ========== Helpers ==========

    private static SpaceIdInjectionSpecification<IContext<IMessage>> CreateSpec(string headerValue)
    {
        var httpContext = new DefaultHttpContext();

        if (headerValue != null)
            httpContext.Request.Headers["X-Space-Id"] = headerValue;

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(x => x.HttpContext).Returns(httpContext);

        return new SpaceIdInjectionSpecification<IContext<IMessage>>(accessor.Object);
    }

    private static Mock<IContext<IMessage>> CreateContext(IMessage message)
    {
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(message);
        return context;
    }

    // ========== Test Helper Classes ==========

    private class TestDirectSpaceScopedRequest : IRequest, ISpaceScoped
    {
        public int? SpaceId { get; set; }
    }

    private class TestExplicitSpaceScopedCommand : ICommand, ISpaceScoped
    {
        int? ISpaceScoped.SpaceId => 99;
    }

    private class TestNonSpaceScopedRequest : IRequest { }

    private class TestNonNullableSpaceScopedRequest : IRequest, ISpaceScoped
    {
        public int SpaceId { get; set; }
        int? ISpaceScoped.SpaceId => SpaceId;
    }
}
