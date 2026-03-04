using Sprache;

namespace Squid.Core.VariableSubstitution.Templates
{
    abstract class TemplateToken : IInputToken
    {
        public Position InputPosition { get; set; }
    }
}
