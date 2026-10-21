<script setup>
defineProps({ error: { type: String, default: "" }, busy: Boolean });
defineEmits(["retry"]);
</script>

<template>
    <div v-if="error" class="notice error" role="alert">
        <span>{{ error }}</span>
        <button type="button" :disabled="busy" @click="$emit('retry')">Retry</button>
    </div>
</template>
