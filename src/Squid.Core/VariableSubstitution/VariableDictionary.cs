using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Squid.Core.VariableSubstitution.Templates;

namespace Squid.Core.VariableSubstitution
{
    public class VariableDictionary
    {
        readonly Dictionary<string, string> variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Binding binding;

        public VariableDictionary()
        {
        }

        Binding Binding
        {
            get
            {
                return binding ?? (binding = PropertyListBinder.CreateFrom(variables));
            }
        }

        public void Set(string name, string value)
        {
            if (name == null) return;
            variables[name] = value;
            binding = null;
        }

        public string this[string name]
        {
            get { return Get(name); }
            set { Set(name, value); }
        }

        public void SetStrings(string variableName, IEnumerable<string> values, string separator = ",")
        {
            var value = string.Join(separator, values.Where(v => !string.IsNullOrWhiteSpace(v)));
            Set(variableName, value);
        }

        public void SetPaths(string variableName, IEnumerable<string> values)
        {
            SetStrings(variableName, values, Environment.NewLine);
        }

        public string GetRaw(string variableName)
        {
            string variable;
            if (variables.TryGetValue(variableName, out variable) && variable != null)
                return variable;

            return null;
        }

        public string Get(string variableName, string defaultValue = null)
        {
            string error;
            return Get(variableName, out error, defaultValue);
        }

        public string Get(string variableName, out string error, string defaultValue = null)
        {
            error = null;
            string variable;
            if (!variables.TryGetValue(variableName, out variable) || variable == null)
                return defaultValue;

            return Evaluate(variable, out error);
        }

        public string Evaluate(string expressionOrVariableOrText, out string error, bool haltOnError = true)
        {
            error = null;
            if (expressionOrVariableOrText == null) return null;

            Template template;
            if (!TemplateParser.TryParseTemplate(expressionOrVariableOrText, out template, out error, haltOnError))
                return expressionOrVariableOrText;

            using (var writer = new StringWriter())
            {
                string[] missingTokens;
                TemplateEvaluator.Evaluate(template, Binding, writer, out missingTokens);
                if (missingTokens.Any())
                {
                    var tokenList = string.Join(", ", missingTokens.Select(token => "'" + token + "'"));
                    error = string.Format("The following tokens were unable to be evaluated: {0}", tokenList);
                }

                return writer.ToString();
            }
        }

        public bool EvaluateTruthy(string expressionOrVariableOrText)
        {
            string error;
            var result = Evaluate(expressionOrVariableOrText, out error);
            return string.IsNullOrWhiteSpace(error) && result != null && TemplateEvaluator.IsTruthy(result);
        }

        public string Evaluate(string expressionOrVariableOrText)
        {
            string error;
            return Evaluate(expressionOrVariableOrText, out error);
        }

        public List<string> GetStrings(string variableName, params char[] separators)
        {
            separators = separators ?? new char[0];
            if (separators.Length == 0) separators = new[] { ',' };

            var value = Get(variableName);
            if (string.IsNullOrWhiteSpace(value))
                return new List<string>();

            var values = value.Split(separators)
                .Select(v => v.Trim())
                .Where(v => v != "");

            return values.ToList();
        }

        public List<string> GetPaths(string variableName)
        {
            return GetStrings(variableName, '\r', '\n');
        }

        public bool GetFlag(string variableName, bool defaultValueIfUnset = false)
        {
            bool value;
            var text = Get(variableName);
            if (string.IsNullOrWhiteSpace(text) || !bool.TryParse(text, out value))
            {
                value = defaultValueIfUnset;
            }

            return value;
        }

        public int? GetInt32(string variableName)
        {
            int value;
            var text = Get(variableName);
            if (string.IsNullOrWhiteSpace(text) || !int.TryParse(text, out value))
            {
                return null;
            }

            return value;
        }

        public string Require(string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            var value = Get(name);
            if (string.IsNullOrEmpty(value))
                throw new ArgumentOutOfRangeException("name", "The variable '" + name + "' is required but no value is set.");
            return value;
        }

        public List<string> GetNames()
        {
            return variables.Keys.ToList();
        }
    }
}
