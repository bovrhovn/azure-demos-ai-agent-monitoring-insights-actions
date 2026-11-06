<script setup>
import { onMounted, onUnmounted, ref } from "vue";
import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { request, timestamp } from "../analytics";
import QueryNotice from "./QueryNotice.vue";
import StatusBadge from "./StatusBadge.vue";
import PaginationControls from "./PaginationControls.vue";

const props = defineProps({
    endpoint: { type: String, required: true },
    hub: { type: String, required: true },
    trigger: { type: String, required: true }
});
const data = ref(null);
const error = ref("");
const busy = ref(false);
const status = ref("Connecting live feed...");
const connected = ref(false);
const connection = new HubConnectionBuilder().withUrl(props.hub)
    .withAutomaticReconnect([0, 2000, 10000, 30000]).configureLogging(LogLevel.Warning).build();
const controller = new AbortController();
let refreshPending = false;
let retryTimer;

// Reconcile persisted history again if an event arrives during an in-flight request.
async function refresh(page = data.value?.page ?? 1) {
    if (controller.signal.aborted) return;
    if (busy.value) {
        refreshPending = true;
        return;
    }
    busy.value = true;
    do {
        refreshPending = false;
        try {
            data.value = await request(props.endpoint, { trigger: props.trigger, page }, controller.signal);
            page = data.value.page;
            error.value = "";
        } catch (failure) {
            if (!controller.signal.aborted) {
                error.value = failure.message;
                console.error("Execution history refresh failed", failure);
            }
            refreshPending = false;
        }
    } while (refreshPending && !controller.signal.aborted);
    busy.value = false;
}

function showConnected() {
    if (connection.state === HubConnectionState.Connected) {
        connected.value = true;
        status.value = "Live feed connected";
    }
}

connection.on("ExecutionChanged", execution => {
    if (execution.trigger === props.trigger) refresh();
});
connection.onreconnecting(() => {
    connected.value = false;
    status.value = "Reconnecting live feed...";
});
connection.onreconnected(async () => {
    await refresh();
    showConnected();
});
connection.onclose(() => {
    connected.value = false;
    status.value = "Live feed disconnected. Reload to reconnect.";
});
async function start() {
    try {
        await connection.start();
        await refresh();
        showConnected();
    } catch (failure) {
        if (!controller.signal.aborted) {
            status.value = "Live feed unavailable. Retrying...";
            console.error("SignalR connection failed", failure);
            retryTimer = setTimeout(start, 10000);
        }
    }
}
onMounted(() => { refresh(); start(); });
onUnmounted(() => {
    controller.abort();
    clearTimeout(retryTimer);
    connection.stop();
});
</script>

<template>
    <div class="section-title">
        <h2>Scheduled execution history</h2>
        <span id="live-status" class="connection-status" :class="{ connected }" role="status">{{ status }}</span>
    </div>
    <QueryNotice :error="error" :busy="busy" @retry="refresh()" />
    <div :aria-busy="busy">
        <div class="table-scroll" tabindex="0" role="region" aria-label="Scrollable execution history">
            <table class="execution-table">
                <thead><tr><th scope="col">Execution / correlation</th><th scope="col">Started / UTC</th><th scope="col">Completed / UTC</th><th scope="col">Status</th><th scope="col">Outcome</th></tr></thead>
                <tbody id="execution-rows">
                    <tr v-for="execution in data?.items ?? []" :key="execution.id" :data-execution-id="execution.id" :data-started-at="execution.startedAt">
                        <td><code>{{ execution.id }}</code><small>{{ execution.correlationId }}</small></td>
                        <td>{{ timestamp(execution.startedAt) }}</td><td>{{ timestamp(execution.completedAt) }}</td>
                        <td><StatusBadge :value="execution.status" /></td><td class="summary">{{ execution.summary }}</td>
                    </tr>
                </tbody>
            </table>
        </div>
        <p v-if="!data && busy" class="empty-state" role="status">Loading execution history...</p>
        <p v-if="data?.totalCount === 0" id="empty-executions" class="empty-state">No executions recorded yet. New runs will appear here.</p>
        <PaginationControls v-if="data" :data="data" :busy="busy" @change="refresh" />
    </div>
</template>
