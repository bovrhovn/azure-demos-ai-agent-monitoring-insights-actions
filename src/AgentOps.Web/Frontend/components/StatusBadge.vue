<script setup>
defineProps({ value: { type: String, required: true } });
</script>

<template>
    <span class="badge" :class="`state-${value.toLowerCase()}`">{{ value }}</span>
</template>
