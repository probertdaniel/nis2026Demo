namespace Affinus.AIS.FaultHandling
{
    using System;
    using System.Text.Json.Serialization;

    /// <summary>Severity values. They are routing instructions: Critical pages someone, Error raises a ticket.</summary>
    public static class Severity
    {
        public const string Critical = "Critical";
        public const string Error = "Error";
        public const string Warning = "Warning";
    }

    /// <summary>Where a failure came from: your workflow, the connector, or the data.</summary>
    public static class Category
    {
        public const string Flow = "Flow";
        public const string Connector = "Connector";
        public const string Data = "Data";
    }

    /// <summary>The fault contract. The first five properties are the contract; Diagnostics is for engineers.</summary>
    public class Fault
    {
        [JsonPropertyName("faultCode")] public string FaultCode { get; set; }
        [JsonPropertyName("source")] public string Source { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; }
        [JsonPropertyName("correlationId")] public string CorrelationId { get; set; }
        [JsonPropertyName("severity")] public string Severity { get; set; }
        [JsonPropertyName("diagnostics")] public FaultDiagnostics Diagnostics { get; set; }

        internal static Fault HandlerFailure(string workflowName, string actionName, string correlationId, string runId, string results, Exception ex)
        {
            return new Fault
            {
                FaultCode = "HANDLER-FAULT-500",
                Source = $"{workflowName} / {(string.IsNullOrEmpty(actionName) ? "unknown action" : actionName)}",
                Message = "The fault handler could not interpret this failure. The raw result is in diagnostics.rawError.",
                CorrelationId = correlationId,
                Severity = FaultHandling.Severity.Error,
                Diagnostics = new FaultDiagnostics
                {
                    WorkflowName = workflowName,
                    ActionName = actionName,
                    RunId = runId,
                    Category = FaultHandling.Category.Flow,
                    Notes = "Handler exception: " + ex.GetType().Name + ": " + ex.Message,
                    RawError = Json.Truncate(results, FaultBuilder.RawErrorLimit),
                    HandlerVersion = FaultBuilder.HandlerVersion,
                },
            };
        }
    }

    /// <summary>Diagnostic detail. Primitive types only, so the designer's generated schema stays simple.</summary>
    public class FaultDiagnostics
    {
        [JsonPropertyName("workflowName")] public string WorkflowName { get; set; }
        [JsonPropertyName("actionName")] public string ActionName { get; set; }
        [JsonPropertyName("actionPath")] public string ActionPath { get; set; }
        [JsonPropertyName("actionType")] public string ActionType { get; set; }
        [JsonPropertyName("connector")] public string Connector { get; set; }
        [JsonPropertyName("operation")] public string Operation { get; set; }
        [JsonPropertyName("target")] public string Target { get; set; }
        [JsonPropertyName("httpStatus")] public int HttpStatus { get; set; }
        [JsonPropertyName("platformCode")] public string PlatformCode { get; set; }
        [JsonPropertyName("systemCode")] public string SystemCode { get; set; }
        [JsonPropertyName("category")] public string Category { get; set; }
        [JsonPropertyName("isTransient")] public bool IsTransient { get; set; }
        [JsonPropertyName("runId")] public string RunId { get; set; }
        [JsonPropertyName("trackingId")] public string TrackingId { get; set; }
        [JsonPropertyName("clientTrackingId")] public string ClientTrackingId { get; set; }
        [JsonPropertyName("startTime")] public string StartTime { get; set; }
        [JsonPropertyName("endTime")] public string EndTime { get; set; }
        [JsonPropertyName("definitionFound")] public bool DefinitionFound { get; set; }
        [JsonPropertyName("definitionFile")] public string DefinitionFile { get; set; }
        [JsonPropertyName("definitionLastModifiedUtc")] public string DefinitionLastModifiedUtc { get; set; }
        [JsonPropertyName("childFaultCode")] public string ChildFaultCode { get; set; }
        [JsonPropertyName("notes")] public string Notes { get; set; }
        [JsonPropertyName("rawError")] public string RawError { get; set; }
        [JsonPropertyName("handlerVersion")] public string HandlerVersion { get; set; }
    }
}
