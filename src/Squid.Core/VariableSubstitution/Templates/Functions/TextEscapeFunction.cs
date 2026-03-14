namespace Squid.Core.VariableSubstitution.Templates.Functions
{
    internal class TextEscapeFunction
    {

        public static string HtmlEscape(string argument, string[] options)
        {
            if (options.Any())
                return null;

            return Escape(argument, HtmlEntityMap);
        }

        public static string XmlEscape(string argument, string[] options)
        {
            if (options.Any())
                return null;

            return Escape(argument, XmlEntityMap);
        }

        public static string JsonEscape(string argument, string[] options)
        {
            if (options.Any())
                return null;

            return Escape(argument, JsonEntityMap);
        }

        static string Escape(string raw, IDictionary<char, string> entities)
        {
            if (raw == null)
                return null;

            return string.Join("", raw.Select(c =>
            {
                string entity;
                if (entities.TryGetValue(c, out entity))
                    return entity;
                return c.ToString();
            }));
        }

        static readonly IDictionary<char, string> HtmlEntityMap = new Dictionary<char, string>
        {
            { '&', "&amp;" },
            { '<', "&lt;" },
            { '>', "&gt;" },
            { '"', "&quot;" },
            { '\'', "&apos;" },
            { '/', "&#x2F;" }
        };

        static readonly IDictionary<char, string> XmlEntityMap = new Dictionary<char, string>
        {
            { '&', "&amp;" },
            { '<', "&lt;" },
            { '>', "&gt;" },
            { '"', "&quot;" },
            { '\'', "&apos;" }
        };

        static readonly IDictionary<char, string> JsonEntityMap = new Dictionary<char, string>
        {
            { '\"', "\\\"" },
            { '\r', "\\\r" },
            { '\t', "\\\t" },
            { '\n', "\\\n" },
            { '\\', "\\\\" }
        };
    }
}
