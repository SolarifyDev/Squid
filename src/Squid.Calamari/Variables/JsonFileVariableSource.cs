namespace Squid.Calamari.Variables;

public sealed class JsonFileVariableSource : IVariableSource
{
    private readonly string _path;

    public JsonFileVariableSource(string path)
    {
        _path = path;
    }

    public string Name => "json-file";

    public IEnumerable<VariableEntry> Load()
    {
        var variables = VariableFileLoader.Load(_path);
        foreach (var (key, value) in variables)
            yield return new VariableEntry(key, value ?? string.Empty, IsSensitive: false, Source: _path);
    }
}
