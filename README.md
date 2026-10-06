# Affinus.AIS.FaultHandling

A generic fault handler for Azure Logic Apps Standard, written as .NET 8 custom code, with the OrderSync demo used in "What Went Wrong? (And Why You Can't Tell)" at the Nordic Integration Summit 2026.

One local function, `HandleFault`, is called from any workflow's Catch scope. It takes the workflow name, the failed action (optional) and `result('Try')`. It then reads that workflow's own `workflow.json` from the Logic App file system, finds the action (including inside scopes, conditions, switches and loops), works out what type of action it is, and parses the error into the standard fault:

```json
{
  "faultCode": "SQL-INSERT-409",
  "source": "OrderSync / Insert_order",
  "message": "Duplicate key in dbo.Orders: SO-9931 (PK_Orders)",
  "correlationId": "run-41217",
  "severity": "Error",
  "diagnostics": { "actionType": "ServiceProvider", "actionPath": "Try/Insert_order", "connector": "sql", "httpStatus": 409, "category": "Data", "isTransient": false, "...": "..." }
}
```

## What is in the workspace

| Folder | Contents |
|---|---|
| `Functions/` | The .NET 8 custom code project. Building it publishes to `LogicApp/lib/custom`. |
| `LogicApp/OrderSync` | The Demo 2 workflow: Try scope (Validate, Enrich from Blob, Insert SQL, Price over HTTP, Notify child workflow), and a Catch scope that calls `HandleFault`, puts the fault on the `faults` queue, responds 500 and terminates the run as Failed. |
| `LogicApp/OrderSyncNoHandling` | The Demo 1 workflow: the same Try scope, no Catch. |
| `LogicApp/NotifyWarehouse` | Child workflow. Returns 400 with `body.reason` for an unknown depot. |
| `LogicApp/PricingApi` | Mock HTTP API. Returns a 422 problem-details response for any SKU starting `BAD`. |
| `setup/` | `setup.ps1` (Azurite container, profile blob and queue; SQL seed) and `seed.sql`. |
| `demo.http` | One request per failure type, for the VS Code REST Client extension. |

## Prerequisites

Windows, VS Code with the Azure Logic Apps (Standard) extension, C# and REST Client extensions, the .NET 8 SDK, Azurite, the Azure CLI, and a SQL Server instance (LocalDB, Developer edition or Azure SQL). Custom code authoring is supported in VS Code on Windows only.

## Run the demo locally

1. Unzip to `C:\Source\Affinus.AIS.FaultHandling`, or update `ProjectDirectoryPath` in both `LogicApp/local.settings.json` and `LogicApp/workflow-designtime/local.settings.json`.
2. Set `Sql_connectionString` in `LogicApp/local.settings.json`.
3. Start Azurite, then run `setup\setup.ps1` (add `-SqlServer <name>` if SQL is not on localhost).
4. Open `FaultHandling.code-workspace`. Build the Functions project (Explorer > right-click `Functions` > Build functions project, or `dotnet build` in `Functions`). Check that `LogicApp/lib/custom/net8/FaultHandling.dll` and `LogicApp/lib/custom/HandleFault/function.json` now exist.
5. Press F5 to start the Logic App.
6. Copy the PricingApi callback URL (right-click `PricingApi/workflow.json` > Overview) into `PricingApiUrl` in `local.settings.json`, and restart.
7. Copy the OrderSync and OrderSyncNoHandling callback URLs into the top of `demo.http`.
8. Send the requests in `demo.http`.

| Request | Fails at | Fault code | Category |
|---|---|---|---|
| 0 | nothing | 200 response | |
| 1 no customer | `Validate_order` (expression) | `FLOW-EXPR-400` | Flow |
| 2 customer 9999 | `Enrich_customer` (Blob) | `BLOB-READ-404` | Data |
| 3 SO-9931 | `Insert_order` (SQL) | `SQL-INSERT-409` | Data |
| 4 SKU BAD-01 | `Price_order` (HTTP) | `HTTP-CALL-422` | Data |
| 5 depot XX | `Notify_warehouse` (child) | `WF-CALL-400` | Data |

Each fault is returned as the 500 response body, written to the `faults` queue in Azurite, logged at Error (Critical for 401/403) with structured properties, and used as the run's terminate error.

## Calling it from your own workflows

Add "Call a local function in this logic app" inside a Catch scope that runs after Try has Failed or TimedOut:

```json
"Handle_fault": {
  "type": "InvokeFunction",
  "inputs": {
    "functionName": "HandleFault",
    "parameters": {
      "workflowName": "@{workflow()?['name']}",
      "actionName": "",
      "results": "@{result('Try')}",
      "correlationId": "@{variables('correlationId')}",
      "runId": "@{workflow()?['run']?['name']}"
    }
  },
  "runAfter": {}
}
```

Leave `actionName` empty to use the first failed action in the results. The function never throws: if it cannot interpret a failure, it returns `HANDLER-FAULT-500` with the raw result in `diagnostics.rawError`.

## How it decides

| Action type | Where the reason is | Code area |
|---|---|---|
| Any action, `InvalidTemplate` | An expression error in `error.message`; missing properties are named | `FLOW-EXPR` |
| `ServiceProvider` (built-in) | Inside the sentence "…error code 'X' and error message 'Y'". SQL and Blob messages are classified further (duplicate key, null column, missing blob, auth, timeout, deadlock) | from `serviceProviderId` and `operationId`, e.g. `SQL-INSERT`, `BLOB-READ` |
| `ApiConnection` (managed) | `outputs.body`, with the `clientRequestId` suffix removed; connector name from `connections.json` | e.g. `SQL-CREATE` |
| `Http` | `outputs.statusCode` and the body: problem details, `error.message`, `message` or plain text | `HTTP-CALL` |
| `Workflow` (child) | The child's Response body. If the child returned a fault, its fault code passes straight through | `WF-CALL` |
| `InvokeFunction` | The exception message | `FUNC-CALL` |
| `Scope`, `If`, `Switch`, `Foreach`, `Until` | Not available: pass `result('<that scope>')` instead | `FLOW-SCOPE` |

Status codes 408, 429, 502, 503 and 504, SQL deadlocks and timeouts are flagged `isTransient: true`.

The root folder is found from, in order: the `FaultHandler__WorkflowRoot` app setting; walking up from the function's own DLL (`lib/custom/net8`) to `host.json`; `ProjectDirectoryPath`; `%HOME%\site\wwwroot`.

## Limits

- The run's error is not on disk, so the Catch scope must pass `result()` in. The handler cannot see inside a failed scope, loop or condition unless you pass that container's result.
- The definition read is the one currently deployed. If a workflow is redeployed while an older run is in progress, they can differ; `diagnostics.definitionLastModifiedUtc` records the file's timestamp.
- Action inputs are deliberately not copied into `rawError`, because they usually carry the business payload.
