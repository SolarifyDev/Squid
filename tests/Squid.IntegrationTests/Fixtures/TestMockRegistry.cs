using System;
using System.Collections.Generic;
using Autofac;
using Halibut;
using Moq;
using Squid.Core.Commands.Tentacle;
using Squid.Core.Services.Tentacle;

namespace Squid.IntegrationTests.Fixtures;

public static class TestMockRegistry
{
    public static ContainerBuilder RegisterMockScriptService(this ContainerBuilder builder)
    {
        var scriptServiceMock = new Mock<IAsyncScriptService>();

        scriptServiceMock
            .Setup(x => x.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ReturnsAsync(new ScriptTicket(Guid.NewGuid().ToString("N")));

        scriptServiceMock
            .SetupSequence(x => x.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(new ScriptTicket("t1"), ProcessState.Running, 0, new List<ProcessOutput>(), 0))
            .ReturnsAsync(new ScriptStatusResponse(new ScriptTicket("t1"), ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        scriptServiceMock
            .Setup(x => x.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(new ScriptTicket("t1"), ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        builder.RegisterInstance(scriptServiceMock.Object).As<IAsyncScriptService>().SingleInstance();
        return builder;
    }

    public static ContainerBuilder RegisterSecuritySetting(this ContainerBuilder builder, string? masterKey = null)
    {
        var setting = new Squid.Core.Settings.Security.SecuritySetting
        {
            VariableEncryption = new Squid.Core.Settings.Security.VariableEncryptionDto
            {
                MasterKey = masterKey ?? Convert.ToBase64String(new byte[32])
            }
        };
        builder.RegisterInstance(setting).As<Squid.Core.Settings.Security.SecuritySetting>().SingleInstance();
        return builder;
    }

    public static ContainerBuilder RegisterTestMocks(this ContainerBuilder builder)
    {
        builder.RegisterMockScriptService();
        builder.RegisterSecuritySetting();
        return builder;
    }

    public static void ApplyTo(ContainerBuilder builder, params Action<ContainerBuilder>[] configurations)
    {
        foreach (var config in configurations)
        {
            config(builder);
        }
    }
}
