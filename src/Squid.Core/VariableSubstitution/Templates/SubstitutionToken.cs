namespace Squid.Core.VariableSubstitution.Templates
{
    class SubstitutionToken : TemplateToken
    {
        readonly ContentExpression expression;

        public SubstitutionToken(ContentExpression expression)
        {
            this.expression = expression;
        }

        public ContentExpression Expression
        {
            get { return expression; }
        }

        public override string ToString()
        {
            return "#{" + Expression + "}";
        }
    }
}
