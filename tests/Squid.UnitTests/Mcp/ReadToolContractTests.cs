using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Net;
using Moq;
using ModelContextProtocol.Protocol;
using Squid.Mcp;
using Squid.Mcp.Tools;
using Squid.Message.Enums;
using Squid.Message.Requests.Mcp;
using Shouldly;
using Squid.Message.Response;
using Xunit;

namespace Squid.UnitTests.Mcp;

public class ReadToolContractTests
{
    private static readonly string[] ExpectedToolNames =
    [
        DiscoveryTools.ListSpacesName,
        DiscoveryTools.ListProjectsName,
        DiscoveryTools.ListEnvironmentsName,
        ReleaseTools.ListReleasesName,
        DeploymentTools.ListDeploymentsName,
        DeploymentTools.GetTaskStatusName
    ];

    private const string LegacySpacesCursor =
        "eyJ2ZXJzaW9uIjoxLCJ0b29sIjoibGlzdF9zcGFjZXMiLCJxdWVyeSI6IiIsImlkIjo0MiwiY3JlYXRlZEF0IjpudWxsfQ";

    private const string LegacyProjectsCursor =
        "eyJ2ZXJzaW9uIjoxLCJ0b29sIjoibGlzdF9wcm9qZWN0cyIsInF1ZXJ5Ijoic3BhY2VJZD0xXHUwMDI2a2V5d29yZFNoYTI1Nj1hMTcyY2VkY2FlNDc0NzRiNjE1YzU0ZDUxMGE1ZDg0YThkZWEzMDMyZTk1ODU4NzQzMGI0MTM1MzhiZTNmMzMzIiwiaWQiOjQyLCJjcmVhdGVkQXQiOm51bGx9";

    private const string LegacyEnvironmentsCursor =
        "eyJ2ZXJzaW9uIjoxLCJ0b29sIjoibGlzdF9lbnZpcm9ubWVudHMiLCJxdWVyeSI6InNwYWNlSWQ9MVx1MDAyNnByb2plY3RJZD0yXHUwMDI2a2V5d29yZFNoYTI1Nj0zZjNlZjc4NmIzNGQ2ZGQ3MTZlMTgxMmM4Yjc0YTdhMGUxZjA1YWE1ZjMyMzA1ODhmNmY1YmNkMDBjNmM4MzkyIiwiaWQiOjQyLCJjcmVhdGVkQXQiOm51bGx9";

    private const string LegacyReleasesCursor =
        "eyJ2ZXJzaW9uIjoxLCJ0b29sIjoibGlzdF9yZWxlYXNlcyIsInF1ZXJ5Ijoic3BhY2VJZD0xXHUwMDI2cHJvamVjdElkPTIiLCJpZCI6NDIsImNyZWF0ZWRBdCI6IjIwMjYtMTEtMjNUMDE6MDI6MDMrMDA6MDAifQ";

    private const string LegacyDeploymentsCursor =
        "eyJ2ZXJzaW9uIjoxLCJ0b29sIjoibGlzdF9kZXBsb3ltZW50cyIsInF1ZXJ5Ijoic3BhY2VJZD0xXHUwMDI2cHJvamVjdElkPTJcdTAwMjZlbnZpcm9ubWVudElkPTMiLCJpZCI6NDIsImNyZWF0ZWRBdCI6IjIwMjYtMTEtMjNUMDE6MDI6MDMrMDA6MDAifQ";

    [Fact]
    public void ListTools_ExposesOnlyAgreedReadContracts()
    {
        ReadToolCatalog.Tools.Select(tool => tool.Name).ShouldBe(ExpectedToolNames);
        ReadToolCatalog.Tools.Select(tool => tool.Sort).ShouldBe(new[]
        {
            ReadToolSort.Id,
            ReadToolSort.Id,
            ReadToolSort.Id,
            ReadToolSort.CreatedAt,
            ReadToolSort.CreatedAt,
            ReadToolSort.None
        });

        var tools = ReadToolCatalog.ToolDefinitions.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        tools.Keys.ShouldBe(ExpectedToolNames);

        foreach (var tool in tools.Values)
        {
            tool.Annotations.ShouldNotBeNull(customMessage: $"{tool.Name} must declare read-only annotations");
            tool.Annotations.ReadOnlyHint.ShouldBe(true);
            tool.OutputSchema.ShouldNotBeNull(customMessage: $"{tool.Name} must declare an output schema");
            tool.Description.ShouldNotBeNullOrWhiteSpace();
        }

        AssertRequired(tools[DiscoveryTools.ListSpacesName]);
        AssertRequired(tools[DiscoveryTools.ListProjectsName], "spaceId");
        AssertRequired(tools[DiscoveryTools.ListEnvironmentsName], "spaceId", "projectId");
        AssertRequired(tools[ReleaseTools.ListReleasesName], "spaceId", "projectId");
        AssertRequired(tools[DeploymentTools.ListDeploymentsName], "spaceId", "projectId", "environmentId");
        AssertRequired(tools[DeploymentTools.GetTaskStatusName], "spaceId", "taskId");

        var pageSize = Property(tools[DiscoveryTools.ListSpacesName], "pageSize");
        pageSize.GetProperty("type").GetString().ShouldBe("integer");
        pageSize.GetProperty("minimum").GetInt32().ShouldBe(1);
        pageSize.GetProperty("maximum").GetInt32().ShouldBe(100);
        pageSize.GetProperty("default").GetInt32().ShouldBe(20);

        AssertOutputFields(tools[DiscoveryTools.ListSpacesName], ["items", "nextCursor"]);
        AssertOutputFields(tools[DiscoveryTools.ListProjectsName], ["items", "nextCursor"]);
        AssertOutputFields(tools[DiscoveryTools.ListEnvironmentsName], ["items", "nextCursor"]);
        AssertOutputFields(tools[ReleaseTools.ListReleasesName], ["items", "nextCursor"]);
        AssertOutputFields(tools[DeploymentTools.ListDeploymentsName],
            ["items", "nextCursor", "latestSuccessfulDeployment"]);
        AssertOutputFields(tools[DeploymentTools.GetTaskStatusName],
            ["taskId", "deploymentId", "state", "startedAt", "completedAt"]);

        AssertItemFields(tools[DiscoveryTools.ListSpacesName], "id", "name");
        AssertItemFields(tools[DiscoveryTools.ListProjectsName], "id", "name", "slug");
        AssertItemFields(tools[DiscoveryTools.ListEnvironmentsName], "id", "name");
        AssertItemFields(tools[ReleaseTools.ListReleasesName], "id", "version", "channelId", "createdAt");
        AssertItemFields(tools[DeploymentTools.ListDeploymentsName],
            "id", "releaseId", "currentReleaseVersion", "taskId", "state", "createdAt", "completedAt");

        var renderedSchemas = string.Join('\n', tools.Values.Select(tool => tool.OutputSchema!.Value.GetRawText()));
        foreach (var forbidden in new[] { "log", "script", "variable", "credential", "apiKey", "errorMessage" })
            renderedSchemas.ShouldNotContain(forbidden, Case.Insensitive);
    }

    [Fact]
    public async Task CallTool_MapsExpectedErrorAndOmitsSensitiveFields()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(value => value.RequestAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
                It.IsAny<ListVisibleProjectsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListVisibleProjectsResponse
            {
                Code = HttpStatusCode.NotFound,
                Msg = "sensitive database lookup detail"
            });

        var result = await ReadToolCatalog.InvokeAsync(
            DiscoveryTools.ListProjectsName,
            Args(("spaceId", 42)),
            mediator.Object,
            CancellationToken.None);

        AssertError(result, ReadToolErrors.NotFoundOrForbidden);
        result.Content.ShouldNotContain(content => content.ToString()!.Contains("sensitive", StringComparison.OrdinalIgnoreCase));
        result.StructuredContent!.Value.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["code", "message"]);

        mediator
            .Setup(value => value.RequestAsync<ListVisibleSpacesRequest, ListVisibleSpacesResponse>(
                It.IsAny<ListVisibleSpacesRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sensitive exception text"));

        var failure = await ReadToolCatalog.InvokeAsync(
            DiscoveryTools.ListSpacesName,
            Args(),
            mediator.Object,
            CancellationToken.None);

        AssertError(failure, ReadToolErrors.TemporaryFailure);
        failure.StructuredContent!.Value.GetRawText().ShouldNotContain("sensitive", Case.Insensitive);
    }

    [Fact]
    public async Task Cursor_FromDifferentQuery_IsInvalid()
    {
        var position = new ScopedReadPosition(104, null);
        var cursor = ReadToolCursor.Encode(
            DiscoveryTools.ListProjectsName,
            ReadToolSort.Id,
            ReadToolCursor.QueryScope(spaceId: 1, keyword: "alpha"),
            position);

        ReadToolCursor.TryDecode(
                cursor,
                DiscoveryTools.ListProjectsName,
                ReadToolSort.Id,
                ReadToolCursor.QueryScope(spaceId: 1, keyword: "alpha"),
                out var decoded)
            .ShouldBeTrue();
        decoded.ShouldBe(position);

        ReadToolCursor.TryDecode(
                cursor,
                DiscoveryTools.ListProjectsName,
                ReadToolSort.Id,
                ReadToolCursor.QueryScope(spaceId: 1, keyword: "beta"),
                out _)
            .ShouldBeFalse();

        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var result = await ReadToolCatalog.InvokeAsync(
            DiscoveryTools.ListProjectsName,
            Args(("spaceId", 1), ("keyword", "beta"), ("cursor", cursor)),
            mediator.Object,
            CancellationToken.None);

        AssertError(result, ReadToolErrors.InvalidArgument);
    }

    [Fact]
    public void Cursor_PreservesPreRefactorBytesAndValidatesToolQueryAndSort()
    {
        var createdAt = DateTimeOffset.Parse("2026-11-23T01:02:03Z");
        var projectsScope =
            "spaceId=1&keywordSha256=a172cedcae47474b615c54d510a5d84a8dea3032e958587430b413538be3f333";
        var environmentsScope =
            "spaceId=1&projectId=2&keywordSha256=3f3ef786b34d6dd716e1812c8b74a7a0e1f05aa5f3230588f6f5bcd00c6c8392";
        var releasesScope = "spaceId=1&projectId=2";
        var deploymentsScope = "spaceId=1&projectId=2&environmentId=3";

        var cases = new[]
        {
            (DiscoveryTools.ListSpacesName, ReadToolSort.Id, "", new ScopedReadPosition(42, null),
                LegacySpacesCursor),
            (DiscoveryTools.ListProjectsName, ReadToolSort.Id, projectsScope,
                new ScopedReadPosition(42, null), LegacyProjectsCursor),
            (DiscoveryTools.ListEnvironmentsName, ReadToolSort.Id, environmentsScope,
                new ScopedReadPosition(42, null), LegacyEnvironmentsCursor),
            (ReleaseTools.ListReleasesName, ReadToolSort.CreatedAt, releasesScope,
                new ScopedReadPosition(42, createdAt), LegacyReleasesCursor),
            (DeploymentTools.ListDeploymentsName, ReadToolSort.CreatedAt, deploymentsScope,
                new ScopedReadPosition(42, createdAt), LegacyDeploymentsCursor)
        };

        foreach (var (toolName, sort, scope, position, cursor) in cases)
        {
            ReadToolCursor.Encode(toolName, sort, scope, position).ShouldBe(cursor);
            ReadToolCursor.TryDecode(cursor, toolName, sort, scope, out var decoded).ShouldBeTrue();
            decoded.ShouldBe(position);
        }

        ReadToolCursor.TryDecode(
                LegacyProjectsCursor,
                DiscoveryTools.ListSpacesName,
                ReadToolSort.Id,
                "",
                out _)
            .ShouldBeFalse();
        ReadToolCursor.TryDecode(
                LegacyProjectsCursor,
                DiscoveryTools.ListProjectsName,
                ReadToolSort.Id,
                projectsScope + "0",
                out _)
            .ShouldBeFalse();
        ReadToolCursor.TryDecode(
                LegacyReleasesCursor,
                ReleaseTools.ListReleasesName,
                ReadToolSort.Id,
                releasesScope,
                out _)
            .ShouldBeFalse();
        Should.Throw<ArgumentException>(() =>
            ReadToolCursor.Encode(
                DeploymentTools.GetTaskStatusName,
                ReadToolSort.None,
                "",
                new ScopedReadPosition(42, null)));
    }

    [Fact]
    public void OutputSchemas_AcceptSuccessAndErrorSamples()
    {
        var tools = ReadToolCatalog.ToolDefinitions.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        foreach (var tool in tools.Values)
        {
            var schema = tool.OutputSchema!.Value;
            schema.GetProperty("type").GetString().ShouldBe("object");

            var variants = schema.GetProperty("oneOf");
            variants.GetArrayLength().ShouldBe(2);
            var successSchema = variants[0];
            var errorSchema = variants[1];

            AssertSchemaValid(successSchema, SuccessSample(tool.Name));
            AssertSchemaValid(errorSchema, ErrorSample(ReadToolErrors.NotFoundOrForbidden));
            AssertSchemaValid(schema, SuccessSample(tool.Name));
            AssertSchemaValid(schema, ErrorSample(ReadToolErrors.TemporaryFailure));
            AssertSchemaInvalid(schema, JsonSerializer.SerializeToElement(new
            {
                code = "UNKNOWN",
                message = "not a defined error"
            }));
            AssertSchemaInvalid(schema, AddUnexpectedField(SuccessSample(tool.Name)));
        }
    }

    [Fact]
    public void Cursor_HashesUnicodeQueryScopeAndKeepsCursorBounded()
    {
        var keyword = string.Concat(Enumerable.Repeat("界🙂", 4096));
        var position = new ScopedReadPosition(42, null);
        var scope = ReadToolCursor.QueryScope(spaceId: 1, keyword: keyword);
        var cursor = ReadToolCursor.Encode(DiscoveryTools.ListProjectsName, ReadToolSort.Id, scope, position);

        cursor.Length.ShouldBeLessThan(512);
        scope.ShouldNotContain(keyword);

        ReadToolCursor.TryDecode(
                cursor,
                DiscoveryTools.ListProjectsName,
                ReadToolSort.Id,
                ReadToolCursor.QueryScope(spaceId: 1, keyword: keyword),
                out var decoded)
            .ShouldBeTrue();
        decoded.ShouldBe(position);

        ReadToolCursor.TryDecode(
                cursor,
                DiscoveryTools.ListProjectsName,
                ReadToolSort.Id,
                ReadToolCursor.QueryScope(spaceId: 1, keyword: keyword + "x"),
                out _)
            .ShouldBeFalse();
    }

    [Fact]
    public void Cursor_RejectsCanonicallyEquivalentKeywordWithDifferentSearchValue()
    {
        var scope = ReadToolCursor.QueryScope(spaceId: 1, keyword: "caf\u00e9");
        var cursor = ReadToolCursor.Encode(
            DiscoveryTools.ListProjectsName,
            ReadToolSort.Id,
            scope,
            new ScopedReadPosition(42, null));

        ReadToolCursor.TryDecode(
                cursor,
                DiscoveryTools.ListProjectsName,
                ReadToolSort.Id,
                ReadToolCursor.QueryScope(spaceId: 1, keyword: "  CAF\u00c9  "),
                out _)
            .ShouldBeTrue();

        ReadToolCursor.TryDecode(
                cursor,
                DiscoveryTools.ListProjectsName,
                ReadToolSort.Id,
                ReadToolCursor.QueryScope(spaceId: 1, keyword: "cafe\u0301"),
                out _)
            .ShouldBeFalse();
    }

    [Fact]
    public void Cursor_EnforcesSortTimeShapeForTimeAndIdPaging()
    {
        var utc = DateTimeOffset.Parse("2026-11-01T08:00:00Z");
        var nonUtc = DateTimeOffset.Parse("2026-11-01T16:00:00+08:00");
        var timeTools = new[]
        {
            (ReleaseTools.ListReleasesName, ReadToolCursor.QueryScope(spaceId: 1, projectId: 2),
                ReadToolSort.CreatedAt),
            (DeploymentTools.ListDeploymentsName,
                ReadToolCursor.QueryScope(spaceId: 1, projectId: 2, environmentId: 3),
                ReadToolSort.CreatedAt)
        };

        foreach (var (toolName, scope, sort) in timeTools)
        {
            Should.Throw<ArgumentException>(() =>
                ReadToolCursor.Encode(toolName, sort, scope, new ScopedReadPosition(10, null)));
            Should.Throw<ArgumentException>(() =>
                ReadToolCursor.Encode(toolName, sort, scope, new ScopedReadPosition(10, nonUtc)));

            var cursor = ReadToolCursor.Encode(toolName, sort, scope, new ScopedReadPosition(10, utc));
            ReadToolCursor.TryDecode(cursor, toolName, sort, scope, out var decoded).ShouldBeTrue();
            decoded.ShouldBe(new ScopedReadPosition(10, utc));
            ReadToolCursor.TryDecode(
                    TamperCursor(cursor, payload => payload["createdAt"] = null),
                    toolName,
                    sort,
                    scope,
                    out _)
                .ShouldBeFalse();
            ReadToolCursor.TryDecode(
                    TamperCursor(cursor, payload => payload["createdAt"] = nonUtc.ToString("O")),
                    toolName,
                    sort,
                    scope,
                    out _)
                .ShouldBeFalse();
        }

        var idTools = new[]
        {
            (DiscoveryTools.ListSpacesName, ReadToolCursor.QueryScope(), ReadToolSort.Id),
            (DiscoveryTools.ListProjectsName, ReadToolCursor.QueryScope(spaceId: 1, keyword: "alpha"),
                ReadToolSort.Id),
            (DiscoveryTools.ListEnvironmentsName,
                ReadToolCursor.QueryScope(spaceId: 1, projectId: 2, keyword: "alpha"),
                ReadToolSort.Id)
        };

        foreach (var (toolName, scope, sort) in idTools)
        {
            Should.Throw<ArgumentException>(() =>
                ReadToolCursor.Encode(toolName, sort, scope, new ScopedReadPosition(10, utc)));

            var cursor = ReadToolCursor.Encode(toolName, sort, scope, new ScopedReadPosition(10, null));
            ReadToolCursor.TryDecode(cursor, toolName, sort, scope, out var decoded).ShouldBeTrue();
            decoded.ShouldBe(new ScopedReadPosition(10, null));
            ReadToolCursor.TryDecode(
                    TamperCursor(cursor, payload => payload["createdAt"] = utc.ToString("O")),
                    toolName,
                    sort,
                    scope,
                    out _)
                .ShouldBeFalse();
        }
    }

    [Fact]
    public async Task PageSize_RejectsValuesOutsideRange()
    {
        foreach (var pageSize in new[] { 0, 101 })
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            var result = await ReadToolCatalog.InvokeAsync(
                DiscoveryTools.ListSpacesName,
                Args(("pageSize", pageSize)),
                mediator.Object,
                CancellationToken.None);

            AssertError(result, ReadToolErrors.InvalidArgument);
        }
    }

    [Fact]
    public async Task PageSize_DefaultsToTwentyAndForwardsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(value => value.RequestAsync<ListVisibleSpacesRequest, ListVisibleSpacesResponse>(
                It.Is<ListVisibleSpacesRequest>(request => request.PageSize == 20 && request.After == null),
                It.Is<CancellationToken>(token => token == cancellationToken)))
            .ReturnsAsync(new ListVisibleSpacesResponse
            {
                Code = HttpStatusCode.OK,
                Data = new ScopedReadPage<SpaceReadItem>([], null)
            });

        var result = await ReadToolCatalog.InvokeAsync(
            DiscoveryTools.ListSpacesName,
            Args(),
            mediator.Object,
            cancellationToken);

        result.IsError.ShouldNotBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("items").GetArrayLength().ShouldBe(0);
        structured.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Audit_UsesRegisteredToolNameAndActualResultCategory()
    {
        var httpContext = new DefaultHttpContext();
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        using var services = new ServiceCollection()
            .AddSingleton<IHttpContextAccessor>(accessor)
            .BuildServiceProvider();

        var result = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "The request was rejected." }],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                code = ReadToolErrors.NotFoundOrForbidden,
                message = "The request was rejected."
            })
        };

        SquidMcpAudit.Record(services, "client-controlled-name", result, 42);
        httpContext.Items.ContainsKey(SquidMcpAudit.ToolNameItemKey).ShouldBeFalse();
        httpContext.Items[SquidMcpAudit.ResultCategoryItemKey].ShouldBe("not_found_or_forbidden");
        httpContext.Items[SquidMcpAudit.RelatedIdItemKey].ShouldBe("42");

        SquidMcpAudit.Record(services, DiscoveryTools.ListProjectsName, result, 43);
        httpContext.Items[SquidMcpAudit.ToolNameItemKey].ShouldBe(DiscoveryTools.ListProjectsName);
        httpContext.Items[SquidMcpAudit.ResultCategoryItemKey].ShouldBe("not_found_or_forbidden");
        httpContext.Items[SquidMcpAudit.RelatedIdItemKey].ShouldBe("43");
    }

    [Fact]
    public async Task ArgumentValidation_ReturnsInvalidArgumentForMissingOrWrongType()
    {
        var mediator = new Mock<IMediator>(MockBehavior.Strict);

        var missing = await ReadToolCatalog.InvokeAsync(
            DiscoveryTools.ListProjectsName,
            Args(),
            mediator.Object,
            CancellationToken.None);
        var wrongType = await ReadToolCatalog.InvokeAsync(
            DiscoveryTools.ListProjectsName,
            Args(("spaceId", "not-an-integer")),
            mediator.Object,
            CancellationToken.None);

        AssertError(missing, ReadToolErrors.InvalidArgument);
        AssertError(wrongType, ReadToolErrors.InvalidArgument);
    }

    [Fact]
    public async Task ArgumentValidation_RejectsExplicitNullOptionalArguments()
    {
        var mediator = new Mock<IMediator>(MockBehavior.Strict);

        var pageSize = await ReadToolCatalog.InvokeAsync(
            DiscoveryTools.ListSpacesName,
            Args(("pageSize", null)),
            mediator.Object,
            CancellationToken.None);
        var keyword = await ReadToolCatalog.InvokeAsync(
            DiscoveryTools.ListProjectsName,
            Args(("spaceId", 1), ("keyword", null)),
            mediator.Object,
            CancellationToken.None);
        var cursor = await ReadToolCatalog.InvokeAsync(
            DiscoveryTools.ListSpacesName,
            Args(("cursor", null)),
            mediator.Object,
            CancellationToken.None);

        AssertError(pageSize, ReadToolErrors.InvalidArgument);
        AssertError(keyword, ReadToolErrors.InvalidArgument);
        AssertError(cursor, ReadToolErrors.InvalidArgument);
    }

    private static IReadOnlyDictionary<string, JsonElement> Args(params (string Key, object? Value)[] values)
    {
        return values.ToDictionary(
            value => value.Key,
            value => JsonSerializer.SerializeToElement(value.Value),
            StringComparer.Ordinal);
    }

    private static void AssertRequired(Tool tool, params string[] required)
    {
        var property = tool.InputSchema.GetProperty("required");
        property.EnumerateArray().Select(value => value.GetString()).ShouldBe(required);
    }

    private static JsonElement Property(Tool tool, string name)
    {
        return tool.InputSchema.GetProperty("properties").GetProperty(name);
    }

    private static void AssertOutputFields(Tool tool, params string[] expected)
    {
        SuccessOutputSchema(tool).GetProperty("properties").EnumerateObject()
            .Select(property => property.Name).ShouldBe(expected);
    }

    private static void AssertItemFields(Tool tool, params string[] expected)
    {
        var itemSchema = SuccessOutputSchema(tool).GetProperty("properties").GetProperty("items").GetProperty("items");
        itemSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ShouldBe(expected);
    }

    private static JsonElement SuccessOutputSchema(Tool tool)
    {
        return tool.OutputSchema!.Value.GetProperty("oneOf")[0];
    }

    private static void AssertError(CallToolResult result, string code)
    {
        result.IsError.ShouldBe(true);
        result.Content.Count.ShouldBe(1);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe(code);
        structured.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
        result.Content.Single().ShouldBeOfType<TextContentBlock>().Text.ShouldNotBeNullOrWhiteSpace();
    }

    private static JsonElement SuccessSample(string toolName)
    {
        return toolName switch
        {
            DiscoveryTools.ListSpacesName => JsonSerializer.SerializeToElement(new
            {
                items = new[] { new { id = 1, name = "Space" } },
                nextCursor = (string)null
            }),
            DiscoveryTools.ListProjectsName => JsonSerializer.SerializeToElement(new
            {
                items = new[] { new { id = 1, name = "Project", slug = "project" } },
                nextCursor = (string)null
            }),
            DiscoveryTools.ListEnvironmentsName => JsonSerializer.SerializeToElement(new
            {
                items = new[] { new { id = 1, name = "Environment" } },
                nextCursor = (string)null
            }),
            ReleaseTools.ListReleasesName => JsonSerializer.SerializeToElement(new
            {
                items = new[]
                {
                    new
                    {
                        id = 1,
                        version = "1.0.0",
                        channelId = 2,
                        createdAt = "2026-11-01T08:00:00Z"
                    }
                },
                nextCursor = (string)null
            }),
            DeploymentTools.ListDeploymentsName => JsonSerializer.SerializeToElement(new
            {
                items = new[]
                {
                    new
                    {
                        id = 1,
                        releaseId = 2,
                        currentReleaseVersion = "1.0.0",
                        taskId = 3,
                        state = "Success",
                        createdAt = "2026-11-01T08:00:00Z",
                        completedAt = "2026-11-01T08:01:00Z"
                    }
                },
                nextCursor = (string)null,
                latestSuccessfulDeployment = (object)null
            }),
            DeploymentTools.GetTaskStatusName => JsonSerializer.SerializeToElement(new
            {
                taskId = 3,
                deploymentId = 1,
                state = "Executing",
                startedAt = "2026-11-01T08:00:00Z",
                completedAt = (string)null
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(toolName), toolName, null)
        };
    }

    private static JsonElement ErrorSample(string code)
    {
        return JsonSerializer.SerializeToElement(new
        {
            code,
            message = "The request could not be completed."
        });
    }

    private static JsonElement AddUnexpectedField(JsonElement sample)
    {
        var node = JsonNode.Parse(sample.GetRawText())!.AsObject();
        node["unexpected"] = true;
        return JsonSerializer.SerializeToElement(node);
    }

    private static string TamperCursor(string cursor, Action<JsonObject> mutate)
    {
        var padded = cursor.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        var payload = JsonNode.Parse(Convert.FromBase64String(padded))!.AsObject();
        mutate(payload);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static void AssertSchemaValid(JsonElement schema, JsonElement value)
    {
        var error = ValidateSchema(schema, value, "$");
        error.ShouldBeNull();
    }

    private static void AssertSchemaInvalid(JsonElement schema, JsonElement value)
    {
        ValidateSchema(schema, value, "$").ShouldNotBeNull();
    }

    private static string ValidateSchema(JsonElement schema, JsonElement value, string path)
    {
        if (schema.ValueKind == JsonValueKind.True) return null;
        if (schema.ValueKind == JsonValueKind.False) return $"{path}: false schema";

        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            var matches = oneOf.EnumerateArray()
                .Count(candidate => ValidateSchema(candidate, value, path) == null);
            if (matches != 1) return $"{path}: oneOf matched {matches} variants";
        }

        if (schema.TryGetProperty("anyOf", out var anyOf) &&
            !anyOf.EnumerateArray().Any(candidate => ValidateSchema(candidate, value, path) == null))
        {
            return $"{path}: no anyOf variant matched";
        }

        if (schema.TryGetProperty("type", out var type) && !MatchesType(type, value))
            return $"{path}: type mismatch";

        if (schema.TryGetProperty("enum", out var allowed) &&
            !allowed.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
        {
            return $"{path}: value is not in enum";
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value))
            return $"{path}: const mismatch";

        if (value.ValueKind == JsonValueKind.String && schema.TryGetProperty("minLength", out var minLength) &&
            value.GetString()!.Length < minLength.GetInt32())
        {
            return $"{path}: string shorter than minLength";
        }

        if (value.ValueKind == JsonValueKind.Number && schema.TryGetProperty("minimum", out var minimum) &&
            value.GetInt64() < minimum.GetInt64())
        {
            return $"{path}: number below minimum";
        }

        if (value.ValueKind == JsonValueKind.Number && schema.TryGetProperty("maximum", out var maximum) &&
            value.GetInt64() > maximum.GetInt64())
        {
            return $"{path}: number above maximum";
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var error = ValidateSchema(itemSchema, item, $"{path}[{index++}]");
                if (error != null) return error;
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.TryGetProperty("properties", out var propertySchemas)
                ? propertySchemas
                : default;

            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var requiredName in required.EnumerateArray().Select(item => item.GetString()))
                {
                    if (!value.TryGetProperty(requiredName!, out _))
                        return $"{path}: missing required property {requiredName}";
                }
            }

            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object &&
                    properties.TryGetProperty(property.Name, out var propertySchema))
                {
                    var error = ValidateSchema(propertySchema, property.Value, $"{path}.{property.Name}");
                    if (error != null) return error;
                }
                else if (schema.TryGetProperty("additionalProperties", out var additional) &&
                         additional.ValueKind == JsonValueKind.False)
                {
                    return $"{path}: unexpected property {property.Name}";
                }
            }
        }

        return null;
    }

    private static bool MatchesType(JsonElement type, JsonElement value)
    {
        if (type.ValueKind == JsonValueKind.Array)
            return type.EnumerateArray().Any(candidate => MatchesType(candidate, value));

        return type.GetString() switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false
        };
    }
}
