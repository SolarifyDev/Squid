using System;
using System.Collections.Generic;
using System.Text;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class CalamariPayloadBuilderTests
{
    private readonly Mock<IYamlNuGetPacker> _packer = new();
    private readonly CalamariPayloadBuilder _builder;

    public CalamariPayloadBuilderTests()
    {
        _builder = new CalamariPayloadBuilder(_packer.Object);
    }

    // === PackageFileName ===

    [Fact]
    public void Build_SetsPackageFileName_UsingReleaseVersion()
    {
        var payload = _builder.Build(CreateRequest(releaseVersion: "2.5.0"));

        payload.PackageFileName.ShouldBe("squid.2.5.0.nupkg");
    }

    // === NuGet package creation ===

    [Fact]
    public void Build_WithFiles_CallsPackerAndStoresBytes()
    {
        var expectedBytes = new byte[] { 1, 2, 3 };
        _packer.Setup(p => p.CreateNuGetPackageFromYamlBytes(
                It.IsAny<Dictionary<string, byte[]>>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(expectedBytes);

        var files = new Dictionary<string, byte[]> { ["deployment.yaml"] = Array.Empty<byte>() };
        var payload = _builder.Build(CreateRequest(files: files));

        payload.PackageBytes.ShouldBe(expectedBytes);
    }

    [Fact]
    public void Build_WithNoFiles_PackageBytesIsEmpty()
    {
        var payload = _builder.Build(CreateRequest());

        payload.PackageBytes.ShouldBeEmpty();
        _packer.Verify(p => p.CreateNuGetPackageFromYamlBytes(
            It.IsAny<Dictionary<string, byte[]>>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // === Variable file creation ===

    [Fact]
    public void Build_WithNoVariables_VariableBytesIsEmptyJson()
    {
        var payload = _builder.Build(CreateRequest());

        var json = Encoding.UTF8.GetString(payload.VariableBytes);
        json.ShouldBe("{}");
    }

    [Fact]
    public void Build_WithSensitiveVariable_SetsPasswordAndSensitiveBytes()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "Secret", Value = "hunter2", IsSensitive = true }
        };

        var payload = _builder.Build(CreateRequest(variables: variables));

        payload.SensitivePassword.ShouldNotBeNullOrEmpty();
        payload.SensitiveBytes.ShouldNotBeEmpty();
    }

    [Fact]
    public void Build_WithNoSensitiveVariables_PasswordIsEmpty()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "Env", Value = "prod", IsSensitive = false }
        };

        var payload = _builder.Build(CreateRequest(variables: variables));

        payload.SensitivePassword.ShouldBe(string.Empty);
    }

    // === FillTemplate ===

    [Fact]
    public void FillTemplate_SubstitutesAllPlaceholders()
    {
        var payload = new CalamariPayload
        {
            PackageFileName = "squid.1.0.0.nupkg",
            PackageBytes = Array.Empty<byte>(),
            VariableBytes = Array.Empty<byte>(),
            SensitiveBytes = Array.Empty<byte>(),
            SensitivePassword = "secret-pass",
            TemplateBody = "pkg={{PackageFilePath}} var={{VariableFilePath}} sens={{SensitiveVariableFile}} pw={{SensitiveVariablePassword}}"
        };

        var result = payload.FillTemplate("/pkg.nupkg", "/vars.json", "/sensitive.json");

        result.ShouldBe("pkg=/pkg.nupkg var=/vars.json sens=/sensitive.json pw=secret-pass");
    }

    [Fact]
    public void FillTemplate_NoSensitivePassword_SetsSensitivePathToEmpty()
    {
        var payload = new CalamariPayload
        {
            SensitivePassword = string.Empty,
            TemplateBody = "{{SensitiveVariableFile}}"
        };

        var result = payload.FillTemplate("/pkg.nupkg", "/vars.json", "/sensitive.json");

        result.ShouldBe(string.Empty);
    }

    [Fact]
    public void Build_EmbeddedTemplate_ContainsToolPreflightChecks_AndArgumentSplatting()
    {
        var payload = _builder.Build(CreateRequest());

        payload.TemplateBody.ShouldContain("Get-Command \"squid-calamari\"");
        payload.TemplateBody.ShouldContain("Get-Command \"kubectl\"");
        payload.TemplateBody.ShouldContain("Get-Command \"bash\"");
        payload.TemplateBody.ShouldContain("$commandArgs = @(");
        payload.TemplateBody.ShouldContain("& $squidCalamari.Source @commandArgs");
    }

    // === Helpers ===

    private static ScriptExecutionRequest CreateRequest(
        string releaseVersion = "1.0.0",
        Dictionary<string, byte[]> files = null,
        List<VariableDto> variables = null)
    {
        return new ScriptExecutionRequest
        {
            Machine = new Squid.Core.Persistence.Entities.Deployments.Machine { Name = "test" },
            ExecutionMode = ExecutionMode.PackagedPayload,
            ReleaseVersion = releaseVersion,
            Files = files ?? new Dictionary<string, byte[]>(),
            Variables = variables ?? new List<VariableDto>()
        };
    }
}
