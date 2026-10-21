<script setup>
import { useAnalyticsQuery } from "../useAnalyticsQuery";
import { timestamp } from "../analytics";
import QueryNotice from "./QueryNotice.vue";
import StatusBadge from "./StatusBadge.vue";

const props = defineProps({ endpoint: { type: String, required: true } });
const { data, error, busy, load } = useAnalyticsQuery(props.endpoint);
</script>

<template>
    <div :aria-busy="busy">
        <QueryNotice :error="error" :busy="busy" @retry="load()" />
        <p v-if="busy && !data" class="empty-state" role="status">Loading schedule...</p>
        <div v-if="data" class="schedule-details">
            <div>
                <span class="eyebrow">RECURRING JOB</span>
                <h2>{{ data.recurringJobId }}</h2>
                <StatusBadge :value="data.enabled ? 'Enabled' : 'Disabled'" />
            </div>
            <dl>
                <div><dt>CRON / UTC</dt><dd><code>{{ data.cron }}</code></dd></div>
                <div><dt>NEXT EXECUTION / UTC</dt><dd><code>{{ timestamp(data.nextExecution) || "Not scheduled" }}</code></dd></div>
                <div><dt>LAST DISPATCH / UTC</dt><dd><code>{{ timestamp(data.lastExecution) || "Not yet dispatched" }}</code></dd></div>
            </dl>
        </div>
    </div>
</template>
