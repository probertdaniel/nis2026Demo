namespace Affinus.AIS.FaultHandling
{
    using System;
    using System.Globalization;
    using System.Text.Encodings.Web;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    /// <summary>Null-safe, case-insensitive helpers over System.Text.Json.Nodes. Nothing here throws on odd input.</summary>
    internal static class Json
    {
        private static readonly JsonDocumentOptions DocOptions = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        /// <summary>Serialiser options that keep apostrophes and quotes readable in rawError.</summary>
        public static readonly JsonSerializerOptions Readable = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        // Keys that commonly carry a human-readable reason, in the order we trust them.
        private static readonly string[] MessageKeys = { "detail", "message", "errorMessage", "error_description", "reason", "title", "description" };
        private static readonly string[] NestedKeys = { "error", "errors", "innerError", "body", "fault" };

        public static JsonNode Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonNode.Parse(text, null, DocOptions); }
            catch (JsonException) { return null; }
        }

        public static JsonNode Prop(JsonNode node, string name)
        {
            if (!(node is JsonObject obj) || name == null) return null;
            if (obj.TryGetPropertyValue(name, out var exact)) return exact;
            foreach (var kv in obj)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return null;
        }

        public static JsonNode Path(JsonNode node, params string[] names)
        {
            foreach (var n in names)
            {
                node = Prop(node, n);
                if (node == null) return null;
            }
            return node;
        }

        public static string Str(JsonNode node)
        {
            if (node == null) return null;
            if (node is JsonValue v)
            {
                if (v.TryGetValue<string>(out var s)) return s;
                return v.ToJsonString();
            }
            return node.ToJsonString();
        }

        public static int Int(JsonNode node)
        {
            var s = Str(node);
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
        }

        /// <summary>A body that arrived as a JSON string is parsed; anything else is returned unchanged.</summary>
        public static JsonNode Unwrap(JsonNode node)
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var s))
            {
                var t = s.TrimStart();
                if (t.StartsWith("{") || t.StartsWith("[")) return Parse(s) ?? node;
            }
            return node;
        }

        /// <summary>Finds the most likely human-readable reason anywhere in a response body.</summary>
        public static string FindMessage(JsonNode node, int depth = 0)
        {
            node = Unwrap(node);
            if (node == null || depth > 4) return null;
            if (node is JsonValue)
            {
                var s = Str(node);
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    var m = FindMessage(item, depth + 1);
                    if (m != null) return m;
                }
                return null;
            }
            foreach (var key in MessageKeys)
            {
                if (Prop(node, key) is JsonValue value)
                {
                    var s = Str(value);
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            foreach (var key in NestedKeys)
            {
                var m = FindMessage(Prop(node, key), depth + 1);
                if (m != null) return m;
            }
            return null;
        }

        public static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "...";
        }
    }
}
