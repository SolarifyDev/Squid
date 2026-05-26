using System.IO;
using System.Xml;
using Shouldly;
using Squid.Calamari.Commands.Configuration;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Configuration;

/// <summary>
/// G1.2 — pure-function tests for the XDT transformer that backs
/// <c>ConfigurationTransformsStep</c>. Pins the operator-observable behaviour
/// of Microsoft's standard <c>Microsoft.Web.Xdt</c> engine via our thin
/// wrapper. The wrapper exists to:
///   1. Centralise error handling (XDT throws raw XmlException on malformed)
///   2. Provide a pure-function API (TransformResult record vs side-effects)
///   3. Be testable in isolation (the step is a thin pipeline orchestrator)
/// </summary>
public sealed class XdtTransformerTests : IDisposable
{
    private readonly string _workDir;

    public XdtTransformerTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"xdt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public void Transform_SetAttributeWithLocatorMatch_AppliesChange()
    {
        var basePath = WriteFile("web.config", """
            <?xml version="1.0"?>
            <configuration>
              <appSettings>
                <add key="EnvironmentName" value="Development" />
              </appSettings>
            </configuration>
            """);

        var transformPath = WriteFile("web.Production.config", """
            <?xml version="1.0"?>
            <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
              <appSettings>
                <add key="EnvironmentName" value="Production" xdt:Transform="SetAttributes" xdt:Locator="Match(key)" />
              </appSettings>
            </configuration>
            """);

        var result = XdtTransformer.Transform(basePath, transformPath);

        result.Succeeded.ShouldBeTrue();

        var transformed = File.ReadAllText(basePath);
        transformed.ShouldContain("value=\"Production\"",
            customMessage: "XDT SetAttributes Match(key) should rewrite the Development → Production value in-place.");
    }

    [Fact]
    public void Transform_InsertElement_AddsNewChild()
    {
        var basePath = WriteFile("app.config", """
            <?xml version="1.0"?>
            <configuration>
              <appSettings>
                <add key="A" value="1" />
              </appSettings>
            </configuration>
            """);
        var transformPath = WriteFile("app.Production.config", """
            <?xml version="1.0"?>
            <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
              <appSettings>
                <add key="B" value="2" xdt:Transform="Insert" />
              </appSettings>
            </configuration>
            """);

        var result = XdtTransformer.Transform(basePath, transformPath);

        result.Succeeded.ShouldBeTrue();
        var transformed = File.ReadAllText(basePath);
        transformed.ShouldContain("key=\"A\"");
        transformed.ShouldContain("key=\"B\"");
    }

    [Fact]
    public void Transform_MissingBaseFile_FailureResult_DoesNotThrow()
    {
        var transformPath = WriteFile("web.Production.config", """
            <?xml version="1.0"?>
            <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform" />
            """);

        var result = XdtTransformer.Transform(Path.Combine(_workDir, "nonexistent.config"), transformPath);

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldContain("base", Case.Insensitive,
            customMessage: "Missing base file MUST be reported as failure-reason mentioning 'base' so the operator knows which side is missing.");
    }

    [Fact]
    public void Transform_MissingTransformFile_FailureResult_DoesNotThrow()
    {
        var basePath = WriteFile("web.config", "<configuration />");

        var result = XdtTransformer.Transform(basePath, Path.Combine(_workDir, "nonexistent.transform"));

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldContain("transform", Case.Insensitive);
    }

    [Fact]
    public void Transform_MalformedXmlInBase_FailureResult_NoCrash()
    {
        var basePath = WriteFile("web.config", "<not really xml");
        var transformPath = WriteFile("web.Production.config", """
            <?xml version="1.0"?>
            <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform" />
            """);

        var result = XdtTransformer.Transform(basePath, transformPath);

        result.Succeeded.ShouldBeFalse(
            customMessage: "Malformed base XML MUST be caught + reported as failure, not propagate as raw XmlException — operator gets a clean error in the deploy log.");
    }

    [Fact]
    public void Transform_NoXdtNamespaceInTransform_ProducesEffectivelyIdentityCopy()
    {
        // Edge case: operator writes a transform file that DOESN'T declare the
        // xdt: namespace and has no actual XDT directives. XDT engine treats
        // it as identity (no changes). We should report success.
        var basePath = WriteFile("web.config", """
            <?xml version="1.0"?>
            <configuration>
              <appSettings>
                <add key="A" value="1" />
              </appSettings>
            </configuration>
            """);
        var transformPath = WriteFile("web.Production.config", """
            <?xml version="1.0"?>
            <configuration />
            """);

        var result = XdtTransformer.Transform(basePath, transformPath);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Transform_PreservesXmlDeclaration()
    {
        // Operator's web.config typically has <?xml version="1.0"?>. Microsoft's
        // XmlTransformableDocument preserves it but our wrapper's write path
        // could accidentally drop it. Pin the round-trip.
        var basePath = WriteFile("web.config", """
            <?xml version="1.0" encoding="UTF-8"?>
            <configuration>
              <appSettings>
                <add key="A" value="old" />
              </appSettings>
            </configuration>
            """);
        var transformPath = WriteFile("web.Production.config", """
            <?xml version="1.0"?>
            <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
              <appSettings>
                <add key="A" value="new" xdt:Transform="SetAttributes" xdt:Locator="Match(key)" />
              </appSettings>
            </configuration>
            """);

        var result = XdtTransformer.Transform(basePath, transformPath);

        result.Succeeded.ShouldBeTrue();
        var transformed = File.ReadAllText(basePath);
        transformed.ShouldContain("<?xml",
            customMessage: "XML declaration MUST be preserved across transform — some downstream parsers (legacy .NET FX configManager) require it.");
        transformed.ShouldContain("value=\"new\"");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_workDir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
