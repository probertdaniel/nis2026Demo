//------------------------------------------------------------
// Affinus - generic fault handler for Logic Apps Standard.
// Called from a workflow's Catch scope with "Call a local function in this logic app".
//------------------------------------------------------------

namespace Affinus.AIS.FaultHandling
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Azure.Functions.Extensions.Workflows;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Turns a failed action's raw result into the standard five-field fault, plus diagnostics.
    /// It reads the calling workflow's own workflow.json to find the failed action and work out its type.
    /// </summary>
    public class HandleFault
    {
        private readonly ILogger<HandleFault> logger;

        public HandleFault(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<HandleFault>();
        }

        /// <param name="workflowName">The calling workflow: pass @{workflow()?['name']}.</param>
        /// <param name="actionName">The failed action. Pass an empty string to use the first failed action in the results.</param>
        /// <param name="results">The scope's results: pass @{result('Try')}. A single action result object is also accepted.</param>
        /// <param name="correlationId">The correlation ID created at the trigger.</param>
        /// <param name="runId">The run: pass @{workflow()?['run']?['name']}.</param>
        [Function("HandleFault")]
        public Task<Fault> Run([WorkflowActionTrigger] string workflowName, string actionName, string results, string correlationId, string runId)
        {
            Fault fault;
            try
            {
                fault = FaultBuilder.Build(workflowName, actionName, results, correlationId, runId, DefinitionStore.Default);
            }
            catch (Exception ex)
            {
                // The handler must never fail the Catch scope: it reports its own failure as a fault instead.
                fault = Fault.HandlerFailure(workflowName, actionName, correlationId, runId, results, ex);
            }

            var level = fault.Severity == Severity.Critical ? LogLevel.Critical : LogLevel.Error;
            this.logger.Log(level,
                "Fault {FaultCode} in {Source}. CorrelationId {CorrelationId}. {FaultMessage} (actionType {ActionType}, connector {Connector}, httpStatus {HttpStatus}, transient {IsTransient})",
                fault.FaultCode, fault.Source, fault.CorrelationId, fault.Message,
                fault.Diagnostics?.ActionType, fault.Diagnostics?.Connector, fault.Diagnostics?.HttpStatus, fault.Diagnostics?.IsTransient);

            return Task.FromResult(fault);
        }
    }
}
