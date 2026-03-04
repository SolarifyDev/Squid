namespace Squid.Calamari.Variables;

public sealed class EncryptedJsonFileVariableSource : IVariableSource
{
    private readonly string _path;
    private readonly string _password;

    public EncryptedJsonFileVariableSource(string path, string password)
    {
        _path = path;
        _password = password;
    }

    public string Name => "encrypted-json-file";

    public IEnumerable<VariableEntry> Load()
    {
        var variables = VariableFileLoader.LoadSensitive(_path, _password);
        foreach (var (key, value) in variables)
            yield return new VariableEntry(key, value ?? string.Empty, IsSensitive: true, Source: _path);
    }
}
