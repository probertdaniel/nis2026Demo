namespace Affinus.AIS.FaultHandling
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text.Json.Nodes;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Builds the standard fault from a failed action result.
    /// The run result says what went wrong; the workflow definition says what kind of action it was,
    /// and therefore where that kind of action hides its status code and its reason.
    /// </summary>
    internal static class FaultBuilder
    {
        public const string HandlerVersion = "1.0.0";
        public const int RawErrorLimit = 4000;
        private const int MessageLimit = 500;

        private static readonly HashSet<string> Containers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Scope", "If", "Switch", "Foreach", "Until" };

        private static readonly Dictionary<string, string> Areas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sql"] = "SQL", ["azureblob"] = "BLOB", ["azurequeues"] = "QUEUE", ["azuretables"] = "TABLE",
            ["servicebus"] = "SB", ["eventhub"] = "EH", ["azurefile"] = "FILE", ["ftp"] = "FTP", ["sftp"] = "SFTP",
            ["http"] = "HTTP", ["workflow"] = "WF", ["office365"] = "O365", ["sharepointonline"] = "SPO",
        };

        private static readonly Dictionary<string, string> Operations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["insertRow"] = "INSERT", ["updateRows"] = "UPDATE", ["deleteRows"] = "DELETE", ["getRows"] = "GET",
            ["executeQuery"] = "QUERY", ["executeStoredProcedure"] = "PROC",
            ["readBlob"] = "READ", ["readBlobFromUri"] = "READ", ["uploadBlob"] = "WRITE", ["deleteBlob"] = "DELETE",
            ["putMessage"] = "PUT", ["sendMessage"] = "SEND", ["sendMessages"] = "SEND",
        };

        /// <summary>What the parsers work out. Turned into the Fault at the end.</summary>
        private sealed class Parsed
        {
            public string Area = "FLOW";
            public string Op = "ACTION";
            public int Status;
            public string Message;
            public string Category;
            public bool Transient;
            public string SystemCode;
            public string Connector;
            public string Operation;
            public string Target;
            public string ChildFaultCode;
            public string ChildSeverity;
        }

        public static Fault Build(string workflowName, string actionName, string results, string correlationId, string runId, DefinitionStore store)
        {
            var notes = new List<string>();
            var entry = SelectResult(Json.Parse(results), actionName, notes);
            var name = !string.IsNullOrWhiteSpace(actionName) ? actionName : Json.Str(Json.Prop(entry, "name"));

            var d = new FaultDiagnostics
            {
                WorkflowName = workflowName,
                ActionName = name,
                RunId = runId,
                TrackingId = Json.Str(Json.Prop(entry, "trackingId")),
                ClientTrackingId = Json.Str(Json.Prop(entry, "clientTrackingId")),
                StartTime = Json.Str(Json.Prop(entry, "startTime")),
                EndTime = Json.Str(Json.Prop(entry, "endTime")),
                HandlerVersion = HandlerVersion,
            };

            // 1. Find the action in the deployed definition.
            LocatedAction located = null;
            if (store.Root == null)
            {
                notes.Add("Logic App root folder not found; set the FaultHandler__WorkflowRoot app setting");
            }
            else
            {
                var file = store.LoadWorkflow(workflowName);
                if (file == null)
                {
                    notes.Add($"workflow.json not found for '{workflowName}' under {store.Root}");
                }
                else
                {
                    d.DefinitionFile = file.FilePath;
                    d.DefinitionLastModifiedUtc = file.LastWriteUtc.ToString("o");
                    located = ActionLocator.Find(file.Node, name);
                    if (located == null) notes.Add($"action '{name}' not found in the deployed definition; parsed from the run result only");
                }
            }

            // 2. Read the common parts of the result.
            var error = Json.Prop(entry, "error");
            var outputs = Json.Prop(entry, "outputs");
            var inputs = Json.Prop(entry, "inputs");
            var status = Json.Str(Json.Prop(entry, "status"));
            var platformCode = Json.Str(Json.Prop(error, "code")) ?? Json.Str(Json.Prop(entry, "code"));
            var errorMessage = Json.Str(Json.Prop(error, "message"));

            var type = located?.Type;
            if (string.IsNullOrEmpty(type) && string.Equals(platformCode, "ServiceProviderActionFailed", StringComparison.OrdinalIgnoreCase)) type = "ServiceProvider";
            d.ActionType = string.IsNullOrEmpty(type) ? "Unknown" : type;
            d.ActionPath = located?.Path ?? name;
            d.DefinitionFound = located != null;

            var p = new Parsed { Status = Json.Int(Json.Prop(outputs, "statusCode")) };
            if (p.Status == 0) p.Status = StatusFromName(Json.Str(Json.Prop(entry, "code")));

            // 3. Parse by type. Expression failures can happen in any action, so they are checked first.
            var def = located?.Definition;
            if (IsExpressionFailure(platformCode, errorMessage)) ParseExpression(name, errorMessage, p);
            else if (Containers.Contains(type ?? string.Empty)) ParseContainer(type, name, p);
            else
            {
                switch ((type ?? string.Empty).ToLowerInvariant())
                {
                    case "serviceprovider": ParseServiceProvider(def, inputs, outputs, errorMessage, p); break;
                    case "apiconnection":
                    case "apiconnectionwebhook": ParseManaged(def, outputs, errorMessage, store, p); break;
                    case "http":
                    case "httpwebhook": ParseHttp(def, inputs, outputs, errorMessage, p); break;
                    case "workflow": ParseChild(def, outputs, errorMessage, p); break;
                    case "invokefunction": ParseFunction(def, errorMessage, p); break;
                    default: ParseGeneric(type, outputs, errorMessage, p); break;
                }
            }

            if (string.Equals(status, "TimedOut", StringComparison.OrdinalIgnoreCase)
                || string.Equals(platformCode, "ActionTimedOut", StringComparison.OrdinalIgnoreCase))
            {
                p.Status = 504;
                p.Transient = true;
                p.Category = Category.Connector;
                p.Message = "Timed out. " + (p.Message ?? errorMessage);
            }

            // 4. Assemble the fault.
            var statusKnown = p.Status != 0;
            if (!statusKnown) p.Status = 500;
            if (p.Category == null) p.Category = statusKnown ? CategoryForStatus(p.Status) : Category.Flow;
            if (p.Status == 408 || p.Status == 429 || p.Status == 502 || p.Status == 503 || p.Status == 504) p.Transient = true;

            d.Connector = p.Connector;
            d.Operation = p.Operation;
            d.Target = p.Target;
            d.HttpStatus = p.Status;
            d.PlatformCode = platformCode;
            d.SystemCode = p.SystemCode;
            d.Category = p.Category;
            d.IsTransient = p.Transient;
            d.ChildFaultCode = p.ChildFaultCode;
            d.RawError = RawError(status, error, outputs);
            if (entry == null) notes.Add("no action result was available");
            d.Notes = notes.Count == 0 ? null : string.Join("; ", notes);

            return new Fault
            {
                FaultCode = p.ChildFaultCode ?? Code(p.Area, p.Op, p.Status),
                Source = $"{workflowName} / {name ?? "unknown action"}",
                Message = Json.Truncate(Clean(p.Message ?? errorMessage ?? "The action failed without an error message."), MessageLimit),
                CorrelationId = correlationId,
                Severity = p.ChildSeverity ?? ((p.Status == 401 || p.Status == 403) ? Severity.Critical : Severity.Error),
                Diagnostics = d,
            };
        }

        // ---------- result selection ----------

        private static JsonNode SelectResult(JsonNode node, string actionName, List<string> notes)
        {
            if (node == null)
            {
                notes.Add("no results were passed; pass @{result('Try')}");
                return null;
            }
            var items = node is JsonArray arr ? arr.Where(i => i != null).ToList() : new List<JsonNode> { node };

            if (!string.IsNullOrWhiteSpace(actionName))
            {
                var named = items.FirstOrDefault(i => string.Equals(Json.Str(Json.Prop(i, "name")), actionName, StringComparison.OrdinalIgnoreCase));
                if (named != null) return named;
                notes.Add($"'{actionName}' was not in the results; used the first failed action instead");
            }

            bool Failed(JsonNode i)
            {
                var s = Json.Str(Json.Prop(i, "status"));
                return string.Equals(s, "Failed", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "TimedOut", StringComparison.OrdinalIgnoreCase);
            }

            // Prefer the action that actually failed over a scope that failed because something inside it did.
            return items.FirstOrDefault(i => Failed(i) && !string.Equals(Json.Str(Json.Path(i, "error", "code")), "ActionFailed", StringComparison.OrdinalIgnoreCase))
                ?? items.FirstOrDefault(Failed)
                ?? items.FirstOrDefault();
        }

        // ---------- parsers ----------

        private static bool IsExpressionFailure(string platformCode, string message)
        {
            return string.Equals(platformCode, "InvalidTemplate", StringComparison.OrdinalIgnoreCase)
                || (message ?? string.Empty).IndexOf("template language expression", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Your workflow: an expression that could not be evaluated, usually a missing property.</summary>
        private static void ParseExpression(string name, string message, Parsed p)
        {
            p.Area = "FLOW"; p.Op = "EXPR"; p.Status = 400; p.Category = Category.Flow; p.Connector = "expression";
            p.SystemCode = "InvalidTemplate";
            var property = Rx(message, @"property '(?<v>[^']+)' (doesn't|does not) exist");
            var expression = Rx(message, @"expression '(?<v>.+?)' cannot be evaluated");
            if (property != null)
            {
                p.Message = $"Expression in {name} failed: property '{property}' does not exist in the data" + (expression != null ? $" ({expression})" : string.Empty);
            }
            else
            {
                p.Message = Regex.Replace(message ?? "Expression failed.", @"\s*Please see https?://\S+ for usage details\.?'?\.?", string.Empty);
            }
        }

        /// <summary>A scope, condition or loop failed because an action inside it failed.</summary>
        private static void ParseContainer(string type, string name, Parsed p)
        {
            p.Area = "FLOW"; p.Op = "SCOPE"; p.Status = 500; p.Category = Category.Flow;
            p.Message = $"An action inside the {type} '{name}' failed. Pass result('{name}') to the handler to identify it.";
        }

        /// <summary>Built-in connectors: SQL, Blob, Queues, Service Bus and so on. The reason is inside a sentence.</summary>
        private static void ParseServiceProvider(JsonObject def, JsonNode inputs, JsonNode outputs, string errorMessage, Parsed p)
        {
            var cfg = Json.Path(def, "inputs", "serviceProviderConfiguration") ?? Json.Prop(inputs, "serviceProviderConfiguration");
            p.Connector = LastSegment(Json.Str(Json.Prop(cfg, "serviceProviderId"))) ?? "serviceprovider";
            p.Operation = Json.Str(Json.Prop(cfg, "operationId"));
            p.Area = AreaFor(p.Connector);
            p.Op = OpFor(p.Operation);

            // "The service provider action failed with error code 'X' and error message 'Y'."
            var m = Regex.Match(errorMessage ?? string.Empty, @"error code '(?<code>[^']*)' and error message '(?<msg>.*)'", RegexOptions.Singleline);
            var inner = m.Success ? m.Groups["msg"].Value.Trim().TrimEnd('.') + "." : (Json.FindMessage(Json.Prop(outputs, "body")) ?? errorMessage);
            p.SystemCode = m.Success ? m.Groups["code"].Value : null;
            if (p.Status == 0) p.Status = StatusFromName(p.SystemCode);
            p.Message = inner;

            // Parameters as the run evaluated them, falling back to the definition.
            JsonNode Param(string key) => Json.Path(inputs, "parameters", key) ?? Json.Path(def, "inputs", "parameters", key);

            switch (p.Connector.ToLowerInvariant())
            {
                case "sql":
                    p.Target = Json.Str(Param("tableName"));
                    ClassifySql(inner, p);
                    break;
                case "azureblob":
                    p.Target = $"{Json.Str(Param("containerName"))}/{Json.Str(Param("blobName"))}";
                    ClassifyBlob(p.SystemCode, inner, p);
                    break;
                case "azurequeues":
                    p.Target = Json.Str(Param("queueName"));
                    break;
            }
        }

        /// <summary>Managed (Azure-hosted) connectors: status and body are in the outputs, with a clientRequestId suffix.</summary>
        private static void ParseManaged(JsonObject def, JsonNode outputs, string errorMessage, DefinitionStore store, Parsed p)
        {
            var reference = Json.Str(Json.Path(def, "inputs", "host", "connection", "referenceName"));
            var apiId = Json.Str(Json.Path(store.LoadConnections(), "managedApiConnections", reference ?? string.Empty, "api", "id"));
            p.Connector = LastSegment(apiId) ?? reference ?? "managed";
            var method = Json.Str(Json.Path(def, "inputs", "method"));
            p.Operation = (method ?? string.Empty).ToUpperInvariant() + " " + Json.Str(Json.Path(def, "inputs", "path"));
            p.Area = AreaFor(p.Connector);
            p.Op = VerbFor(method);

            var body = Json.Unwrap(Json.Prop(outputs, "body"));
            var message = Json.FindMessage(body) ?? errorMessage;
            p.Message = message == null ? null : Regex.Replace(message, @"\s*clientRequestId:.*$", string.Empty, RegexOptions.Singleline);
            p.SystemCode = Json.Str(Json.Path(body, "error", "code")) ?? Json.Str(Json.Prop(body, "code"));
            if (string.Equals(p.Connector, "sql", StringComparison.OrdinalIgnoreCase)) ClassifySql(p.Message, p);
        }

        /// <summary>HTTP: the response status and whatever body the API chose to send.</summary>
        private static void ParseHttp(JsonObject def, JsonNode inputs, JsonNode outputs, string errorMessage, Parsed p)
        {
            p.Connector = "http"; p.Area = "HTTP"; p.Op = "CALL";
            var method = Json.Str(Json.Prop(inputs, "method")) ?? Json.Str(Json.Path(def, "inputs", "method"));
            var uri = Json.Str(Json.Prop(inputs, "uri")) ?? Json.Str(Json.Path(def, "inputs", "uri"));
            p.Target = HostAndPath(uri);       // never the query string: it can carry a SAS signature
            p.Operation = (method ?? "GET").ToUpperInvariant();

            var body = Json.Unwrap(Json.Prop(outputs, "body"));
            p.SystemCode = Json.Str(Json.Path(body, "error", "code")) ?? Json.Str(Json.Prop(body, "code")) ?? Json.Str(Json.Prop(body, "type"));
            p.Message = Json.FindMessage(body) ?? errorMessage;
            if (outputs == null && p.Status == 0)
            {
                p.Status = 502; p.Transient = true; p.Category = Category.Connector;   // no response at all
            }
        }

        /// <summary>A child workflow. If it returned our own fault shape, pass the child's fault straight through.</summary>
        private static void ParseChild(JsonObject def, JsonNode outputs, string errorMessage, Parsed p)
        {
            p.Connector = "workflow"; p.Area = "WF"; p.Op = "CALL";
            var child = Json.Str(Json.Path(def, "inputs", "host", "workflow", "id"));
            p.Target = child;
            p.Operation = "invoke";
            var body = Json.Unwrap(Json.Prop(outputs, "body"));
            var childCode = Json.Str(Json.Prop(body, "faultCode"));
            if (!string.IsNullOrEmpty(childCode))
            {
                p.ChildFaultCode = childCode;
                p.ChildSeverity = Json.Str(Json.Prop(body, "severity"));
                p.Category = Json.Str(Json.Path(body, "diagnostics", "category"));
                p.Message = $"{child}: {Json.Str(Json.Prop(body, "message"))}";
                return;
            }
            var reason = Json.FindMessage(body) ?? errorMessage;
            p.Message = child == null ? reason : $"{child}: {reason}";
        }

        /// <summary>Custom code: the exception message is in the error.</summary>
        private static void ParseFunction(JsonObject def, string errorMessage, Parsed p)
        {
            p.Connector = "function"; p.Area = "FUNC"; p.Op = "CALL"; p.Category = Category.Flow;
            p.Target = Json.Str(Json.Path(def, "inputs", "functionName"));
            p.Message = errorMessage;
        }

        private static void ParseGeneric(string type, JsonNode outputs, string errorMessage, Parsed p)
        {
            p.Area = "FLOW";
            p.Op = string.IsNullOrEmpty(type) ? "ACTION" : Sanitise(type).ToUpperInvariant();
            p.Message = Json.FindMessage(Json.Prop(outputs, "body")) ?? errorMessage;
        }

        // ---------- connector-specific classification ----------

        private static void ClassifySql(string msg, Parsed p)
        {
            if (msg == null) return;
            if (Has(msg, "PRIMARY KEY", "UNIQUE KEY", "duplicate key"))
            {
                p.Status = 409; p.Category = Category.Data;
                var obj = Rx(msg, @"object '(?<v>[^']+)'");
                var value = Rx(msg, @"duplicate key value is \((?<v>[^)]*)\)");
                var constraint = Rx(msg, @"constraint '(?<v>[^']+)'");
                p.Message = "Duplicate key" + (obj != null ? " in " + obj : string.Empty) + (value != null ? ": " + value : string.Empty)
                          + (constraint != null ? " (" + constraint + ")" : string.Empty);
            }
            else if (Has(msg, "FOREIGN KEY", "REFERENCE constraint", "CHECK constraint")) { p.Status = 422; p.Category = Category.Data; }
            else if (Has(msg, "Cannot insert the value NULL"))
            {
                p.Status = 400; p.Category = Category.Data;
                var column = Rx(msg, @"into column '(?<v>[^']+)'");
                if (column != null) p.Message = $"Required column '{column}' is null";
            }
            else if (Has(msg, "would be truncated")) { p.Status = 400; p.Category = Category.Data; }
            else if (Has(msg, "Login failed", "authentication failed")) { p.Status = 401; p.Category = Category.Connector; }
            else if (Has(msg, "deadlock")) { p.Status = 409; p.Category = Category.Connector; p.Transient = true; }
            else if (Has(msg, "Timeout expired", "timeout period elapsed")) { p.Status = 504; p.Category = Category.Connector; p.Transient = true; }
            else if (Has(msg, "Invalid object name", "Invalid column name")) { p.Status = 400; p.Category = Category.Flow; }
        }

        private static void ClassifyBlob(string code, string msg, Parsed p)
        {
            var c = code ?? string.Empty;
            if (c.Equals("BlobNotFound", StringComparison.OrdinalIgnoreCase) || Has(msg, "specified blob does not exist"))
            { p.Status = 404; p.Category = Category.Data; p.Message = $"Blob not found: {p.Target}"; }
            else if (c.Equals("ContainerNotFound", StringComparison.OrdinalIgnoreCase) || Has(msg, "specified container does not exist"))
            { p.Status = 404; p.Category = Category.Connector; p.Message = $"Container not found for {p.Target}"; }
            else if (c.Equals("BlobAlreadyExists", StringComparison.OrdinalIgnoreCase)) { p.Status = 409; p.Category = Category.Data; }
            else if (c.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase) || c.StartsWith("Authorization", StringComparison.OrdinalIgnoreCase))
            { p.Status = 403; p.Category = Category.Connector; }
            else if (c.Equals("ServerBusy", StringComparison.OrdinalIgnoreCase) || c.Equals("OperationTimedOut", StringComparison.OrdinalIgnoreCase))
            { p.Status = 503; p.Category = Category.Connector; p.Transient = true; }
        }

        // ---------- small helpers ----------

        private static string Code(string area, string op, int status) => $"{Sanitise(area).ToUpperInvariant()}-{Sanitise(op).ToUpperInvariant()}-{status}";

        private static string Sanitise(string s) => Regex.Replace(s ?? "X", "[^A-Za-z0-9]", string.Empty);

        private static string AreaFor(string connector)
        {
            if (string.IsNullOrEmpty(connector)) return "CONN";
            if (Areas.TryGetValue(connector, out var a)) return a;
            var s = Sanitise(connector).ToUpperInvariant();
            return s.Length > 8 ? s.Substring(0, 8) : s;
        }

        private static string OpFor(string operationId)
        {
            if (string.IsNullOrEmpty(operationId)) return "ACTION";
            if (Operations.TryGetValue(operationId, out var o)) return o;
            var verb = Regex.Match(operationId, "^[a-z]+").Value;   // camelCase: take the leading verb
            return (verb.Length > 0 ? verb : Sanitise(operationId)).ToUpperInvariant();
        }

        private static string VerbFor(string method)
        {
            switch ((method ?? string.Empty).ToUpperInvariant())
            {
                case "GET": return "READ";
                case "POST": return "CREATE";
                case "PUT":
                case "PATCH": return "UPDATE";
                case "DELETE": return "DELETE";
                default: return "CALL";
            }
        }

        private static int StatusFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            if (int.TryParse(name, out var n)) return n >= 100 && n < 600 ? n : 0;
            return Enum.TryParse<HttpStatusCode>(name, true, out var code) ? (int)code : 0;
        }

        private static string CategoryForStatus(int status)
        {
            if (status == 401 || status == 403 || status == 408 || status == 429 || status >= 500) return Category.Connector;
            if (status >= 400) return Category.Data;
            return Category.Flow;
        }

        private static string LastSegment(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var parts = id.TrimEnd('/').Split('/');
            return parts[parts.Length - 1];
        }

        private static string HostAndPath(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return null;
            return Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.Host + (u.IsDefaultPort ? string.Empty : ":" + u.Port) + u.AbsolutePath : "(expression)";
        }

        private static bool Has(string s, params string[] needles) =>
            s != null && needles.Any(n => s.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);

        private static string Rx(string input, string pattern)
        {
            if (string.IsNullOrEmpty(input)) return null;
            var m = Regex.Match(input, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return m.Success ? m.Groups["v"].Value : null;
        }

        private static string Clean(string s) => Regex.Replace(s ?? string.Empty, @"\s+", " ").Trim();

        private static string RawError(string status, JsonNode error, JsonNode outputs)
        {
            // Error and outputs only. Inputs are left out because they often carry the business payload.
            var raw = "{\"status\":\"" + (status ?? string.Empty) + "\",\"error\":" + (error?.ToJsonString(Json.Readable) ?? "null")
                    + ",\"outputs\":" + (outputs?.ToJsonString(Json.Readable) ?? "null") + "}";
            return Json.Truncate(raw, RawErrorLimit);
        }
    }
}
