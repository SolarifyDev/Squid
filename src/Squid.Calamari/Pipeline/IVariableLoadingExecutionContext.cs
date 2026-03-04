using Squid.Calamari.Variables;

namespace Squid.Calamari.Pipeline;

public interface IVariableLoadingExecutionContext
{
    string VariablesPath { get; }

    string? SensitivePath { get; }

    string? Password { get; }

    VariableSet? Variables { get; set; }
}
