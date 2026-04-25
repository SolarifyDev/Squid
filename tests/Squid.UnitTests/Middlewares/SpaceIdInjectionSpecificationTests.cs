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

    // ── P0-D.4 (2026-04-24 audit): non-nullable `int SpaceId` — Register* / Generate*
    // commands commonly declare this shape. Pre-fix, the reflection filter (`PropertyType
    // != typeof(int?)`) silently skipped them, so header injection never ran. These
    // tests pin the new behaviour:
    //   - body-zero (typical `{}` body) → header injects
    //   - body-non-zero (caller explicitly supplied) → header does NOT override, same as int?
    //   - header-missing / header-invalid → body-zero stays zero (later auth check rejects)

    [Fact]
    public async Task InjectsSpaceId_NonNullable_WhenBodyZero_AndHeaderPresent()
    {
        var spec = CreateSpec("5");
        var message = new TestNonNullableSpaceScopedRequest();  // SpaceId defaults to 0
        var context = CreateContext(message);

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        message.SpaceId.ShouldBe(5,
            customMessage:
                "non-nullable int SpaceId with body-zero must receive header injection. Pre-fix the " +
                "reflection filter skipped this shape entirely — the whole point of P0-D.4.");
    }

    [Fact]
    public async Task SkipsInjection_NonNullable_WhenBodyNonZero()
    {
        // Mirror of SkipsInjection_WhenSpaceIdAlreadySet for int?. Body-supplied non-
        // zero value is treated as "caller intended this" — matches int? semantics.
        var spec = CreateSpec("5");
        var message = new TestNonNullableSpaceScopedRequest { SpaceId = 3 };
        var context = CreateContext(message);

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        message.SpaceId.ShouldBe(3,
            customMessage: "body-supplied non-zero must be preserved; header does not override");
    }

    [Fact]
    public async Task SkipsInjection_NonNullable_WhenHeaderMissing()
    {
        var spec = CreateSpec(null);
        var message = new TestNonNullableSpaceScopedRequest();
        var context = CreateContext(message);

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        message.SpaceId.ShouldBe(0,
            customMessage: "no header, body-zero stays zero; downstream auth check rejects SpaceOnly perms");
    }

    [Fact]
    public async Task SkipsInjection_NonNullable_WhenHeaderInvalid()
    {
        var spec = CreateSpec("not-a-number");
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

    [Fact]
    public async Task SkipsInjection_WhenSpaceIdIsReadOnly()
    {
        // Defensive edge case: if someone declares ISpaceScoped with a read-only
        // `SpaceId` (e.g. expression-bodied getter), the middleware must silently
        // skip rather than blow up. The reflection filter's `!prop.CanWrite` gate
        // catches this; this test pins the contract.
        var spec = CreateSpec("5");
        var message = new TestReadOnlySpaceScopedCommand();
        var context = CreateContext(message);

        Should.NotThrow(async () => await spec.BeforeExecute(context.Object, CancellationToken.None));

        // Accessor still returns its computed value — middleware didn't throw and didn't corrupt.
        ((ISpaceScoped)message).SpaceId.ShouldBe(7);
    }

    // ========== Test Helper Classes ==========

    private class TestDirectSpaceScopedRequest : IRequest, ISpaceScoped
    {
        public int? SpaceId { get; set; }
    }

    private class TestReadOnlySpaceScopedCommand : ICommand, ISpaceScoped
    {
        // SpaceId is a get-only auto-property — `!prop.CanWrite` short-circuits the
        // middleware. Exercises the CanWrite guard that previously had no test.
        public int? SpaceId { get; } = 7;
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
