using System.Text;

namespace Squid.Core.VariableSubstitution.Templates
{
    class SymbolExpression : ContentExpression
    {
        readonly SymbolExpressionStep[] steps;

        public SymbolExpression(IEnumerable<SymbolExpressionStep> steps)
        {
            this.steps = steps.ToArray();
        }

        public SymbolExpressionStep[] Steps
        {
            get { return steps; }
        }

        public override string ToString()
        {
            var result = new StringBuilder();
            var identifierJoin = "";
            foreach (var step in Steps)
            {
                if (step is Identifier)
                    result.Append(identifierJoin);

                result.Append(step);

                identifierJoin = ".";
            }

            return result.ToString();
        }

        private sealed class StepsEqualityComparer : IEqualityComparer<SymbolExpression>
        {
            public bool Equals(SymbolExpression x, SymbolExpression y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (ReferenceEquals(x, null)) return false;
                if (ReferenceEquals(y, null)) return false;
                if (x.GetType() != y.GetType()) return false;
                return x.steps.SequenceEqual(y.steps);
            }

            public int GetHashCode(SymbolExpression obj)
            {
                return obj.steps?.GetHashCode() ?? 0;
            }
        }

        public static IEqualityComparer<SymbolExpression> StepsComparer { get; } = new StepsEqualityComparer();
    }
}
