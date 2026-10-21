import { createApp } from "vue";
import OverviewPanel from "./components/OverviewPanel.vue";
import SchedulePanel from "./components/SchedulePanel.vue";
import LogsPanel from "./components/LogsPanel.vue";
import ExecutionFeed from "./components/ExecutionFeed.vue";

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
