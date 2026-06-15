using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Security;

public interface IVariableEncryptionService : IScopedDependency
{
    string EncryptAsync(string plainText, int variableSetId);

    Task<string> DecryptAsync(string encryptedText, int variableSetId);

    /// <summary>Synchronous decrypt — DecryptAsync's work is pure CPU (AES-GCM + PBKDF2, no I/O), so
    /// callers on a sync path can decrypt without an async signature. Read-both: a value lacking the
    /// envelope prefix (legacy plaintext) is returned verbatim.</summary>
    string Decrypt(string encryptedText, int variableSetId);

    Task<List<Variable>> EncryptSensitiveVariablesAsync(
        List<Variable> variables, 
        int variableSetId);

    Task<List<Variable>> DecryptSensitiveVariablesAsync(
        List<Variable> variables, 
        int variableSetId);

    bool IsValidEncryptedValue(string encryptedText);
}
