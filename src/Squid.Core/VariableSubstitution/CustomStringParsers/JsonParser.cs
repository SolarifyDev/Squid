using System.Text.Json;
using Squid.Core.VariableSubstitution.Templates;

namespace Squid.Core.VariableSubstitution.CustomStringParsers
{
    internal static class JsonParser
    {
        internal static bool TryParse(Binding parentBinding, string property, out Binding subBinding)
        {
            subBinding = null;

            try
            {
                using var doc = JsonDocument.Parse(parentBinding.Item);
                var root = doc.RootElement;

                switch (root.ValueKind)
                {
                    case JsonValueKind.String:
                    case JsonValueKind.Number:
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        subBinding = new Binding(root.ToString());
                        return true;

                    case JsonValueKind.Array:
                        return TryParseJsonArray(root, property, out subBinding);

                    case JsonValueKind.Object:
                        return TryParseJsonObject(root, property, out subBinding);
                }
            }
            catch (JsonException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            return false;
        }

        internal static bool TryParse(Binding binding, out Binding[] subBindings)
        {
            subBindings = Array.Empty<Binding>();

            try
            {
                using var doc = JsonDocument.Parse(binding.Item);
                var root = doc.RootElement;

                switch (root.ValueKind)
                {
                    case JsonValueKind.Array:
                        return TryParseJsonArray(root, out subBindings);

                    case JsonValueKind.Object:
                        return TryParseJsonObject(root, out subBindings);
                }
            }
            catch (JsonException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            return false;
        }

        static bool TryParseJsonObject(JsonElement obj, out Binding[] subBindings)
        {
            subBindings = obj.EnumerateObject().Select(p =>
            {
                var b = new Binding(p.Name)
                {
                    { "Key", new Binding(p.Name) },
                    { "Value", ConvertElementToBinding(p.Value) }
                };
                return b;
            }).ToArray();
            return true;
        }

        static bool TryParseJsonArray(JsonElement array, out Binding[] subBindings)
        {
            subBindings = array.EnumerateArray().Select(ConvertElementToBinding).ToArray();
            return true;
        }

        private static bool TryParseJsonObject(JsonElement obj, string property, out Binding subBinding)
        {
            subBinding = null;
            if (obj.TryGetProperty(property, out var prop))
            {
                subBinding = ConvertElementToBinding(prop);
                return true;
            }
            // Case-insensitive fallback
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase))
                {
                    subBinding = ConvertElementToBinding(p.Value);
                    return true;
                }
            }
            return true;
        }

        private static bool TryParseJsonArray(JsonElement array, string property, out Binding subBinding)
        {
            int index;
            subBinding = null;

            if (!int.TryParse(property, out index))
                return false;

            var i = 0;
            foreach (var element in array.EnumerateArray())
            {
                if (i == index)
                {
                    subBinding = ConvertElementToBinding(element);
                    return true;
                }
                i++;
            }
            return false;
        }

        static Binding ConvertElementToBinding(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return new Binding(element.GetString() ?? string.Empty);
                case JsonValueKind.Number:
                    return new Binding(element.GetRawText());
                case JsonValueKind.True:
                    return new Binding("True");
                case JsonValueKind.False:
                    return new Binding("False");
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return new Binding(string.Empty);
                default:
                    return new Binding(element.GetRawText());
            }
        }
    }
}
