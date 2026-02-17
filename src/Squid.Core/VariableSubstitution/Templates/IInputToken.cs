using Sprache;

namespace Squid.Core.VariableSubstitution.Templates
{
    interface IInputToken
    {
        Position InputPosition { get; set; }
    }
}
