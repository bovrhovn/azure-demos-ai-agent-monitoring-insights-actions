import { createApp } from "vue";
import OverviewPanel from "./components/OverviewPanel.vue";
import SchedulePanel from "./components/SchedulePanel.vue";
import LogsPanel from "./components/LogsPanel.vue";
import ExecutionFeed from "./components/ExecutionFeed.vue";
import ChatWorkspace from "./components/ChatWorkspace.vue";
import NotificationInbox from "./components/NotificationInbox.vue";

const routes = document.body.dataset;
const panels = [
    ["[data-overview]", OverviewPanel, { endpoint: routes.analyticsOverview }],
    ["[data-schedule]", SchedulePanel, { endpoint: routes.analyticsSchedule }],
    ["[data-logs]", LogsPanel, { endpoint: routes.analyticsLogs }],
    ["[data-execution-feed]", ExecutionFeed, { endpoint: routes.analyticsExecutions, hub: routes.executionHub }]
];

for (const [selector, component, props] of panels) {
    const element = document.querySelector(selector);
    if (element) createApp(component, { ...props, trigger: element.dataset.executionFeed }).mount(element);
}

const chat = document.querySelector("[data-chat]");
if (chat) createApp(ChatWorkspace, {
    endpoint: chat.dataset.endpoint,
    token: chat.dataset.token,
    canSend: chat.dataset.canSend === "true",
    maxLength: Number(chat.dataset.maxLength),
    historyLimit: Number(chat.dataset.historyLimit),
    initialSessionId: chat.dataset.sessionId
}).mount(chat);

const notifications = document.querySelector("[data-notifications]");
if (notifications) createApp(NotificationInbox, {
    endpoint: notifications.dataset.endpoint,
    telemetryTestEndpoint: notifications.dataset.telemetryTestEndpoint,
    webhookTestEndpoint: notifications.dataset.webhookTestEndpoint,
    chatPage: notifications.dataset.chatPage,
    token: notifications.dataset.token,
    canOperate: notifications.dataset.canOperate === "true",
    maxSearchLength: Number(notifications.dataset.maxSearchLength),
    maxCommentLength: Number(notifications.dataset.maxCommentLength),
    maxTestMessageLength: Number(notifications.dataset.maxTestMessageLength)
}).mount(notifications);
