namespace AgentOps.Web;

/// <summary>Application-local routes and virtual asset URLs.</summary>
public static class RouteConstants
{
    public const string Root = "/";
    public const string IndexPage = "/Info/Index";
    public const string ErrorPage = "/Info/Error";
    public const string SchedulesPage = "/Analytics/Schedules";
    public const string ExecutionsPage = "/Analytics/Executions";
    public const string LogsPage = "/Analytics/Logs";
    public const string ChatPage = "/Chat/Index";
    public const string ChatSessions = "/ui/chat/sessions";
    public const string NotificationsPage = "/Notifications/Index";
    public const string Notifications = "/ui/notifications";
    public const string NotificationTelemetryTest = "/tests/telemetry";
    public const string NotificationWebhookTest = "/tests/webhook";
    public const string LegacyIndexPage = "/Index";
    public const string LegacyErrorPage = "/Error";
    public const string LegacySchedulesPage = "/Schedules";
    public const string LegacyExecutionsPage = "/Executions";
    public const string LegacyLogsPage = "/Logs";
    public const string ExecutionHub = "/hubs/executions";
    public const string ExecutionHistory = "/ui/executions";
    public const string AnalyticsExecutions = "/ui/analytics/executions";
    public const string AnalyticsOverview = "/ui/analytics/overview";
    public const string AnalyticsLogs = "/ui/analytics/logs";
    public const string AnalyticsSchedule = "/ui/analytics/schedule";
    public const string ExecutionApi = "/api/executions";
    public const string EventGridWebhook = "/api/webhooks/eventgrid";
    public const string ExecutionById = "/{id}";
    public const string Health = "/health";
    public const string Liveness = "/health/live";
    public const string Readiness = "/health/ready";
    public const string SignInCallback = "/signin-oidc";
    public const string SignOutCallback = "/signout-callback-oidc";
    public const string Favicon = "~/favicon.svg";
    public const string Stylesheet = "~/css/site.css";
    public const string BrowserScript = "~/js/app.js";
}
