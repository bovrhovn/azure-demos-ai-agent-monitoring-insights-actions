import { onMounted, onUnmounted, ref } from "vue";
import { request } from "./analytics";

export function useAnalyticsQuery(endpoint) {
    const data = ref(null);
    const error = ref("");
    const busy = ref(false);
    const controller = new AbortController();

    async function load(page = data.value?.page ?? 1) {
        if (busy.value || controller.signal.aborted) return;
        busy.value = true;
        error.value = "";
        try {
            data.value = await request(endpoint, { page }, controller.signal);
        } catch (failure) {
            if (!controller.signal.aborted) {
                error.value = failure.message;
                console.error("Analytics request failed", failure);
            }
        } finally {
            busy.value = false;
        }
    }

    onMounted(() => load());
    onUnmounted(() => controller.abort());
    return { data, error, busy, load };
}
