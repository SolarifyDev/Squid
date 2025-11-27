using System.ComponentModel;

namespace Squid.Message.Enums;

public enum OperatingSystemType
{
    [Description("win-x64")]
    Windows,
    [Description("osx-x64")]
    MacOs,
    [Description("linux-x64")]
    Linux,
    [Description("linux-arm")]
    Arm,
    [Description("linux-arm64")]
    Arm64
}