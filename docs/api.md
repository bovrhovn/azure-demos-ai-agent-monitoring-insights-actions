# API and live event reference

All application pages and hubs require a reader or operator application role. Execution API routes use a **bearer-only** policy: browser cookies cannot invoke them.

## Routes

| Method | Route | Authorization / result |
|---|---|---|
| GET | `/`, `/Info/Index` | Reader/operator; Application Insights overview |
| GET | `/Info/Error` | Reader/operator; safe error information and request ID |
| GET | `/Analytics/Schedules` | Reader/operator; schedule and scheduled history |
| GET | `/Analytics/Executions` | Reader/operator; endpoint-only live history |
| GET | `/Analytics/Logs` | Reader/operator; telemetry traces |
| POST | `/api/executions` | API bearer token with operator role or exact delegated `Agent.Run` scope |
| GET | `/api/executions` | Same bearer policy; latest endpoint executions |
| GET | `/api/executions/{id}` | Same bearer policy; endpoint record, or 404 |
| GET | `/ui/executions?trigger=Endpoint` | Cookie reader/operator; bounded history for live-feed reconciliation |
| GET | `/ui/executions?trigger=Schedule` | Cookie reader/operator; bounded scheduled history |
| GET | `/ui/analytics/overview` | Cookie reader/operator; metrics and configured lookback |
| GET | `/ui/analytics/schedule` | Cookie reader/operator; configured job and volatile dispatch times |
| GET | `/ui/analytics/executions?trigger=Endpoint&page=1&pageSize=10` | Cookie reader/operator; paged execution history (`Endpoint` or `Schedule`) |
| GET | `/ui/analytics/logs?page=1&pageSize=10` | Cookie reader/operator; paged recent telemetry traces |
| SignalR | `/hubs/executions` | Reader/operator; server-to-client notifications only |
| GET | `/health` | Anonymous; alias for Cosmos readiness |
| GET | `/health/live` | Anonymous; process liveness |
| GET | `/health/ready` | Anonymous; Cosmos execution-history container access |

Application-local paths and callback/asset defaults live in `RouteConstants.cs`.
Health routes execute over internal HTTP without redirecting; public Container
Apps ingress requires HTTPS. All other routes retain their authentication and
transport policies.

The old `/Index`, `/Error`, `/Schedules`, `/Executions` and `/Logs` URLs remain
aliases. Razor hosts compiled Vue single-file components built with Vite. The logs
page contains only a mount point and no server-rendered table or telemetry query;
`LogsPanel.vue` loads, refreshes and pages results through `/ui/analytics/logs`.
Other pages retain their initial read-only view before Vue mounts. All components
call the session-only Minimal APIs. No browser bearer token is stored, and these
read-only endpoints cannot invoke an agent.

## Invoke the agent scaffold

Obtain an access token from an authorized Entra client for this app's audience. Use either the delegated `api://<client-id>/Agent.Run` scope or an app-only token with the operator role. Do not substitute an ID token or a Microsoft Graph access token.

```powershell
# $token contains an API access token from your authorized client.
Invoke-RestMethod 'https://localhost:5080/api/executions' `
    -Method Post `
    -Headers @{ Authorization = "Bearer $token" }
```

No request body or caller-supplied prompt is accepted yet. The endpoint executes the shared handler synchronously and returns HTTP 200 with a durable record:

```json
{
  "id": "generated-execution-id",
  "trigger": "Endpoint",
  "status": "NotImplemented",
  "startedAt": "2026-10-19T10:00:00+00:00",
  "completedAt": "2026-10-19T10:00:00+00:00",
  "requestedBy": "authenticated-caller-object-id",
  "correlationId": "trace-id",
  "summary": "Agent behavior is pending implementation. No model or action was invoked."
}
```

HTTP success means the scaffold handler ran and persisted its outcome. It **does not** mean that an AI action succeeded. Always inspect `status`.

Missing/invalid tokens return 401; authenticated callers without permission return 403. The configured fixed-window rate limiter returns 429. Unexpected failures return safe Problem Details with a trace ID; details remain in structured application logs.

The bearer query API and legacy `/ui/executions` endpoint retain their bounded
array contracts. The Vue analytics APIs return
`{ items, page, pageSize, totalCount, totalPages, limit }`. Paging stays within the
latest `Executions:HistoryLimit` records for a known trigger, or `Insights:LogLimit`
traces, newest first; it does not navigate the entire archive. Defaults come from
`Analytics:PageSize` and `Analytics:MaxPageSize`. Invalid sizes/nonpositive pages
return 400; a page beyond the last available page is clamped to the last page.
Unknown UI trigger values return 400. Unconfigured telemetry returns 503 Problem
Details; query failures return safe 500 Problem Details, never synthetic metrics.
Stored statuses are `Running`, `NotImplemented`, `Failed` and `Canceled`.

## SignalR

The official SignalR JavaScript client connects using the same-origin web session and listens for `ExecutionChanged`. The payload is the same camel-case execution record used by the API.

The handler persists `Running` before publishing it, then persists and publishes the terminal outcome. A notification failure is logged and never rewrites a successful persistence result. The Vue feed filters notifications by trigger and reconciles the current page from Cosmos on start, reconnect and matching events. Events arriving during an in-flight refresh queue another refresh, so live updates cannot be lost or grow the bounded page.

The hub exposes no client methods that execute work. Cross-origin hub requests are rejected even for authenticated sessions. Notifications are not durable; Cosmos is the source of truth. There is no cross-replica backplane in this scaffold, so the deployment is capped at one replica.

## Correlation and cancellation

`correlationId` is the current W3C trace ID where available. `ILogger` scopes carry `ExecutionId`, `Trigger` and `CorrelationId`. Execution summaries avoid raw exception text.

If an API caller disconnects or a cooperative execution times out, the handler attempts to persist `Canceled` with an independent, bounded finalization token, then rethrows cancellation. A failed final write is not reported as a successful execution. A hard process crash can still leave `Running` records; recovery/reconciliation is a future design task.
