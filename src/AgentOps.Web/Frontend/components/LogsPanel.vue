<script setup>
import { useAnalyticsQuery } from "../useAnalyticsQuery";
import { timestamp } from "../analytics";
import QueryNotice from "./QueryNotice.vue";
import StatusBadge from "./StatusBadge.vue";
import PaginationControls from "./PaginationControls.vue";

const props = defineProps({ endpoint: { type: String, required: true } });
const { data, error, busy, load } = useAnalyticsQuery(props.endpoint);
</script>

<template>
    <section class="flow-panel" :aria-busy="busy" aria-labelledby="logs-heading">
        <div class="section-title">
            <div><span class="eyebrow">APPLICATION INSIGHTS / NOT EXECUTION HISTORY</span><h2 id="logs-heading">Recent traces</h2></div>
            <button type="button" :disabled="busy" @click="load()"><span aria-hidden="true">&#8635;</span> Refresh</button>
        </div>
        <QueryNotice :error="error" :busy="busy" @retry="load()" />
        <p v-if="busy && !data" class="empty-state" role="status">Loading traces...</p>
        <template v-if="data">
            <div class="table-scroll" tabindex="0" role="region" aria-label="Scrollable application logs">
                <table>
                    <thead><tr><th scope="col">Timestamp / UTC</th><th scope="col">Severity</th><th scope="col">Message</th><th scope="col">Operation</th></tr></thead>
                    <tbody>
                        <tr v-for="(log, index) in data.items" :key="`${log.timestamp}-${index}`">
                            <td><time :datetime="log.timestamp">{{ timestamp(log.timestamp) }}</time></td>
                            <td><StatusBadge :value="log.severity" /></td>
                            <td class="log-message">{{ log.message }}</td>
                            <td><code>{{ log.operationId }}</code></td>
                        </tr>
                    </tbody>
                </table>
            </div>
            <p v-if="!data.totalCount" class="empty-state">No traces in the configured time window.</p>
            <PaginationControls :data="data" :busy="busy" @change="load" />
        </template>
    </section>
</template>
