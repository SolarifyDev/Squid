namespace Squid.Core.VariableSubstitution.Templates
{
    class FunctionCallExpression : ContentExpression
    {

        readonly bool _filterSyntax;

        public FunctionCallExpression(bool filterSyntax, string function, ContentExpression argument, params TemplateToken[] options)
        {
            Options = options;
            _filterSyntax = filterSyntax;
            Function = function;
            Argument = argument;
        }

        public TemplateToken[] Options { get; }
        public string Function { get; }

        public ContentExpression Argument { get; }

        IInputToken[] GetAllArguments()
        {
            var tokens = new List<IInputToken>();
            if(Argument.InputPosition != null)
                tokens.Add(Argument);

            tokens.AddRange(Options);
            return tokens.ToArray();
        }

        public override string ToString()
        {
            if (_filterSyntax)
                return $"{Argument} | {Function}{(Options.Any() ? " " : "")}{string.Join(" ", Options.Select(t => t.ToString()))}";


            return $"{Function} ({string.Join(", ", GetAllArguments().Select(t => t.ToString()))})";
        }
    }
}
