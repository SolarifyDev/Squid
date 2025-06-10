namespace Squid.Core.Attributes;

[AttributeUsage(AttributeTargets.Class)]
public class ServiceAttribute : Attribute
{
    public Type ContractType { get; }

    public ServiceAttribute(Type contractType)
    {
        ContractType = contractType;
    }
}