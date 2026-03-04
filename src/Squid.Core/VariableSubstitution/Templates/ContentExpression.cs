using Sprache;

namespace Squid.Core.VariableSubstitution.Templates
{
    abstract class ContentExpression : IInputToken
    {
        public Position InputPosition { get; set; }
    }
}
