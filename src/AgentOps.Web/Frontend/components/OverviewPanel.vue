<script setup>
import { computed } from "vue";
import { useAnalyticsQuery } from "../useAnalyticsQuery";
import QueryNotice from "./QueryNotice.vue";

const props = defineProps({ endpoint: { type: String, required: true } });
const { data, error, busy, load } = useAnalyticsQuery(props.endpoint);
const metrics = computed(() => data.value ? [
    { title: "TOTAL REQUESTS", value: data.value.overview.requests, description: "Incoming application traffic" },
    { title: "FAILED REQUESTS", value: data.value.overview.failedRequests, description: "Requests marked unsuccessful" },
    { title: "MEAN RESPONSE", value: data.value.overview.averageDurationMs, description: "Average request duration", unit: "ms" },
    { title: "EXCEPTIONS", value: data.value.overview.exceptions, description: "Recorded application exceptions" }
] : []);
</script>

<template>
    <div :aria-busy="busy">
        <QueryNotice :error="error" :busy="busy" @retry="load()" />
        <p v-if="busy && !data" class="empty-state" role="status">Loading data...</p>
        <section v-if="data" class="metrics" aria-label="Application Insights metrics">
            <article v-for="metric in metrics" :key="metric.title" class="metric">
                <span class="eyebrow">{{ metric.title }}</span>
                <strong>{{ metric.value.toLocaleString(undefined, { maximumFractionDigits: 0 }) }}<small v-if="metric.unit">{{ metric.unit }}</small></strong>
                <span>{{ metric.description }}</span>
            </article>
        </section>
    </div>
</template>
