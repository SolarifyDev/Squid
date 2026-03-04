namespace Squid.Calamari.Variables;

public static class VariableSetFactory
{
    public static VariableSet Create(IEnumerable<IVariableSource> sources)
    {
        var result = new VariableSet();

        foreach (var source in sources ?? Array.Empty<IVariableSource>())
        {
            if (source == null)
                continue;

            result.Merge(source.Load());
        }

        return result;
    }

    public static VariableSet CreateFromFiles(
        string variablesPath,
        string? sensitivePath,
        string? password)
    {
        var sources = new List<IVariableSource>
        {
            new JsonFileVariableSource(variablesPath)
        };

        if (!string.IsNullOrEmpty(sensitivePath) && !string.IsNullOrEmpty(password))
            sources.Add(new EncryptedJsonFileVariableSource(sensitivePath, password));

        return Create(sources);
    }
}
