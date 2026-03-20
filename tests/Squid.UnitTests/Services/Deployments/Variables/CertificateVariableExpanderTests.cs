using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Certificates;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Variables;

public class CertificateVariableExpanderTests
{
    private readonly Mock<ICertificateDataProvider> _certProvider = new();
    private readonly CertificateVariableExpander _expander;

    public CertificateVariableExpanderTests()
    {
        _expander = new CertificateVariableExpander(_certProvider.Object);
    }

    [Fact]
    public async Task ExpandAsync_CertificateVariable_Adds5SubVars()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "MyCert", Value = "42", Type = VariableType.Certificate }
        };

        _certProvider
            .Setup(c => c.GetCertificatesByIdsAsync(It.Is<List<int>>(ids => ids.Contains(42)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Certificate>
            {
                new()
                {
                    Id = 42, Thumbprint = "ABC123", SubjectCommonName = "example.com",
                    CertificateData = "base64pfx", NotAfter = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    HasPrivateKey = true
                }
            });

        await _expander.ExpandAsync(variables, CancellationToken.None);

        variables.Count.ShouldBe(6);
        variables.ShouldContain(v => v.Name == "MyCert.Thumbprint" && v.Value == "ABC123");
        variables.ShouldContain(v => v.Name == "MyCert.SubjectCommonName" && v.Value == "example.com");
        variables.ShouldContain(v => v.Name == "MyCert.Pfx" && v.Value == "base64pfx");
        variables.ShouldContain(v => v.Name == "MyCert.NotAfter");
        variables.ShouldContain(v => v.Name == "MyCert.HasPrivateKey" && v.Value == "True");
    }

    [Fact]
    public async Task ExpandAsync_PfxIsSensitive()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "MyCert", Value = "42", Type = VariableType.Certificate }
        };

        _certProvider
            .Setup(c => c.GetCertificatesByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Certificate>
            {
                new() { Id = 42, Thumbprint = "T", SubjectCommonName = "C", CertificateData = "pfx", NotAfter = DateTimeOffset.UtcNow, HasPrivateKey = false }
            });

        await _expander.ExpandAsync(variables, CancellationToken.None);

        variables.First(v => v.Name == "MyCert.Pfx").IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public async Task ExpandAsync_NonCertificateVariable_Ignored()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "AppName", Value = "Squid", Type = VariableType.String }
        };

        await _expander.ExpandAsync(variables, CancellationToken.None);

        variables.Count.ShouldBe(1);
        _certProvider.Verify(c => c.GetCertificatesByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExpandAsync_InvalidCertId_Skipped()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "MyCert", Value = "not-a-number", Type = VariableType.Certificate }
        };

        await _expander.ExpandAsync(variables, CancellationToken.None);

        variables.Count.ShouldBe(1);
        _certProvider.Verify(c => c.GetCertificatesByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExpandAsync_CertNotFoundInDb_Skipped()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "MyCert", Value = "999", Type = VariableType.Certificate }
        };

        _certProvider
            .Setup(c => c.GetCertificatesByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Certificate>());

        await _expander.ExpandAsync(variables, CancellationToken.None);

        variables.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ExpandAsync_BatchLoadsMultipleCerts()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "Cert1", Value = "1", Type = VariableType.Certificate },
            new() { Name = "Cert2", Value = "2", Type = VariableType.Certificate }
        };

        _certProvider
            .Setup(c => c.GetCertificatesByIdsAsync(It.Is<List<int>>(ids => ids.Count == 2), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Certificate>
            {
                new() { Id = 1, Thumbprint = "T1", SubjectCommonName = "C1", CertificateData = "P1", NotAfter = DateTimeOffset.UtcNow, HasPrivateKey = true },
                new() { Id = 2, Thumbprint = "T2", SubjectCommonName = "C2", CertificateData = "P2", NotAfter = DateTimeOffset.UtcNow, HasPrivateKey = false }
            });

        await _expander.ExpandAsync(variables, CancellationToken.None);

        variables.Count.ShouldBe(12);
        _certProvider.Verify(c => c.GetCertificatesByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExpandAsync_EmptyList_NoOp()
    {
        var variables = new List<VariableDto>();

        await _expander.ExpandAsync(variables, CancellationToken.None);

        variables.ShouldBeEmpty();
        _certProvider.Verify(c => c.GetCertificatesByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
