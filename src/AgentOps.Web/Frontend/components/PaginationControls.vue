<script setup>
import { computed } from "vue";

const props = defineProps({ data: { type: Object, required: true }, busy: Boolean });
defineEmits(["change"]);
const start = computed(() => props.data.totalCount ? (props.data.page - 1) * props.data.pageSize + 1 : 0);
const end = computed(() => Math.min(props.data.page * props.data.pageSize, props.data.totalCount));
</script>

<template>
    <nav class="pagination" aria-label="Results pagination">
        <span class="results-count" role="status">{{ start }}&ndash;{{ end }} of {{ data.totalCount }} &middot; Latest {{ data.limit }} maximum</span>
        <div class="page-controls">
            <button type="button" :disabled="busy || data.page <= 1" @click="$emit('change', data.page - 1)">
                <span aria-hidden="true">&larr;</span> Previous
            </button>
            <span aria-current="page">Page <strong>{{ data.page }}</strong> of {{ data.totalPages }}</span>
            <button type="button" :disabled="busy || data.page >= data.totalPages" @click="$emit('change', data.page + 1)">
                Next <span aria-hidden="true">&rarr;</span>
            </button>
        </div>
    </nav>
</template>
