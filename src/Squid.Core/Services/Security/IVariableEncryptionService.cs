namespace Squid.Core.Services.Security;

public interface IVariableEncryptionService : IScopedDependency
{
    string EncryptAsync(string plainText, int variableSetId);

    Task<string> DecryptAsync(string encryptedText, int variableSetId);

    Task<List<Variable>> EncryptSensitiveVariablesAsync(
        List<Variable> variables, 
        int variableSetId);

    Task<List<Variable>> DecryptSensitiveVariablesAsync(
        List<Variable> variables, 
        int variableSetId);

    bool IsValidEncryptedValue(string encryptedText);
}
