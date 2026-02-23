namespace Squid.Calamari.Variables;

public interface IVariableSource
{
    string Name { get; }

    IEnumerable<VariableEntry> Load();
}
