namespace Affinus.AIS.FaultHandling
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Nodes;

    /// <summary>An action found in a workflow definition.</summary>
    internal sealed class LocatedAction
    {
        public string Name { get; set; }
        /// <summary>Where it sits, e.g. "Try/Insert_order" or "Check_stock/else/Reorder".</summary>
        public string Path { get; set; }
        public string Type { get; set; }
        public JsonObject Definition { get; set; }
    }

    /// <summary>Searches a workflow definition for an action by name, including inside scopes, conditions, switches and loops.</summary>
    internal static class ActionLocator
    {
        public static LocatedAction Find(JsonNode workflow, string actionName)
        {
            if (string.IsNullOrWhiteSpace(actionName)) return null;
            var actions = Json.Path(workflow, "definition", "actions") ?? Json.Prop(workflow, "actions");
            return Search(actions as JsonObject, actionName, string.Empty);
        }

        private static LocatedAction Search(JsonObject actions, string name, string prefix)
        {
            if (actions == null) return null;
            foreach (var kv in actions)
            {
                if (!(kv.Value is JsonObject def)) continue;
                var path = prefix.Length == 0 ? kv.Key : prefix + "/" + kv.Key;
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return new LocatedAction { Name = kv.Key, Path = path, Type = Json.Str(Json.Prop(def, "type")), Definition = def };
                }
                foreach (var child in ChildActionSets(def))
                {
                    var hit = Search(child.Value, name, path + child.Key);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        private static IEnumerable<KeyValuePair<string, JsonObject>> ChildActionSets(JsonObject def)
        {
            if (Json.Prop(def, "actions") is JsonObject body) yield return new KeyValuePair<string, JsonObject>(string.Empty, body);
            if (Json.Path(def, "else", "actions") is JsonObject otherwise) yield return new KeyValuePair<string, JsonObject>("/else", otherwise);
            if (Json.Path(def, "default", "actions") is JsonObject dflt) yield return new KeyValuePair<string, JsonObject>("/default", dflt);
            if (Json.Prop(def, "cases") is JsonObject cases)
            {
                foreach (var c in cases)
                {
                    if (Json.Prop(c.Value, "actions") is JsonObject caseActions) yield return new KeyValuePair<string, JsonObject>("/" + c.Key, caseActions);
                }
            }
        }
    }
}
