using Squid.Message.Enums;

namespace Squid.Message.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class RequiresPermissionAttribute(Permission permission) : Attribute
{
    public Permission Permission { get; } = permission;
}
