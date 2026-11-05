<script setup>
import { nextTick, onMounted, ref } from "vue";

const props = defineProps({
    endpoint: { type: String, required: true },
    telemetryTestEndpoint: { type: String, required: true },
    webhookTestEndpoint: { type: String, required: true },
    chatPage: { type: String, required: true },
    token: { type: String, required: true },
    canOperate: Boolean,
    maxSearchLength: { type: Number, required: true },
    maxCommentLength: { type: Number, required: true },
    maxTestMessageLength: { type: Number, required: true }
});
const items = ref([]);
const search = ref("");
const handled = ref("all");
const sort = ref("desc");
const page = ref(0);
const tokens = ref([null]);
const nextToken = ref(null);
const busy = ref(false);
const error = ref("");
const status = ref("");
const dialog = ref(null);
const commentInput = ref(null);
const completing = ref(null);
const comment = ref("");
const testMessage = ref("Hello from the webhook inbox.");
const activeTest = ref("");
const testReceipt = ref(null);
let returnFocus = null;
const formatDate = value => new Date(value).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
const chatUrl = sessionId => {
    const url = new URL(props.chatPage, window.location.origin);
    url.searchParams.set("sessionId", sessionId);
    return url.href;
};

async function request(url, method = "GET", body) {
    const response = await fetch(url, {
        method, credentials: "same-origin", redirect: "error",
        headers: { Accept: "application/json", "Content-Type": "application/json", RequestVerificationToken: props.token },
        body: body === undefined ? undefined : JSON.stringify(body)
    });
    if (!response.ok) {
        let detail = `The inbox could not be updated (HTTP ${response.status}). Refresh before retrying.`;
        if (response.status === 401 || response.status === 403)
            detail = "Your session expired or access was denied. Reload the page to sign in.";
        else if (response.status === 429) detail = "Too many requests. Wait a minute before trying again.";
        else if ([400, 409, 502, 503, 504].includes(response.status)) {
            const problem = await response.json();
            detail = problem.title ?? detail;
        }
        throw new Error(detail);
    }
    return response.status === 204 ? null : response.json();
}

async function readPage(index) {
    const url = new URL(props.endpoint, window.location.origin);
    url.searchParams.set("search", search.value.trim());
    url.searchParams.set("handled", handled.value);
    url.searchParams.set("sort", sort.value);
    if (tokens.value[index]) url.searchParams.set("continuationToken", tokens.value[index]);
    const result = await request(url);
    items.value = result.items;
    nextToken.value = result.continuationToken;
    page.value = index;
}

async function load(reset = false, index = page.value) {
    if (busy.value) return;
    busy.value = true;
    error.value = "";
    status.value = "";
    if (reset) { tokens.value = [null]; index = 0; }
    try { await readPage(index); }
    catch (failure) { error.value = failure.message; }
    finally { busy.value = false; }
}

function nextPage() {
    tokens.value[page.value + 1] = nextToken.value;
    load(false, page.value + 1);
}

function closeDialog() {
    dialog.value.close();
    completing.value = null;
    returnFocus?.focus();
}

function toggle(item, event) {
    event.target.checked = item.isHandled;
    if (item.isHandled) updateHandled(item, false);
    else {
        returnFocus = event.target;
        completing.value = item;
        comment.value = "";
        dialog.value.showModal();
        nextTick(() => commentInput.value?.focus());
    }
}

async function updateHandled(item, isHandled) {
    if (busy.value) return;
    busy.value = true;
    error.value = "";
    try {
        await request(`${props.endpoint}/${encodeURIComponent(item.id)}/handled`, "POST", {
            isHandled, comment: isHandled ? comment.value : null, confirmed: isHandled
        });
        if (dialog.value.open) closeDialog();
        tokens.value = [null];
        await readPage(0);
        status.value = isHandled ? "Notification marked as handled." : "Notification marked as unhandled.";
    } catch (failure) { error.value = failure.message; }
    finally { busy.value = false; }
}

async function startChat(item) {
    if (busy.value) return;
    busy.value = true;
    error.value = "";
    try {
        const result = await request(`${props.endpoint}/${encodeURIComponent(item.id)}/session`, "POST");
        window.location.assign(chatUrl(result.sessionId));
    } catch (failure) { error.value = failure.message; }
    finally { busy.value = false; }
}

async function runTest(kind) {
    if (busy.value) return;
    busy.value = true;
    activeTest.value = kind;
    testReceipt.value = null;
    error.value = "";
    status.value = "";
    try {
        const result = await request(kind === "telemetry" ? props.telemetryTestEndpoint : props.webhookTestEndpoint,
            "POST", kind === "webhook" ? { message: testMessage.value } : undefined);
        testReceipt.value = { ...result, kind };
        if (kind === "webhook") {
            search.value = "";
            handled.value = "all";
            sort.value = "desc";
            tokens.value = [null];
            await readPage(0);
        }
    } catch (failure) {
        error.value = testReceipt.value
            ? "The test notification was accepted, but the inbox could not refresh. Refresh before sending another test."
            : failure.message;
    } finally { busy.value = false; activeTest.value = ""; }
}

onMounted(() => load(true));
</script>

<template>
    <section v-if="canOperate" class="notification-test-tools" aria-labelledby="notification-tests-title" :aria-busy="!!activeTest">
        <header><div><p class="eyebrow">PIPELINE CHECK / OPERATOR TOOLS</p><h2 id="notification-tests-title">Send a signal. Follow the evidence.</h2></div>
            <span class="notification-test-badge">Manual tests only</span></header>
        <div class="notification-test-grid">
            <article class="notification-test-card">
                <div class="notification-test-card-heading"><span class="notification-test-icon" aria-hidden="true">&#9889;</span>
                    <div><span class="eyebrow">01 / TELEMETRY</span><h3>Application Insights</h3></div></div>
                <p>Generate one handled exception and one warning trace with a random test ID and the current UTC time.</p>
                <button type="button" class="notification-test-button" :disabled="busy" @click="runTest('telemetry')">
                    <span>{{ activeTest === "telemetry" ? "Generating..." : "Generate telemetry" }}</span><span aria-hidden="true">&#8599;</span>
                </button>
                <small>Clearly marked synthetic data. Ingestion may take a few minutes.</small>
            </article>
            <form class="notification-test-card" @submit.prevent="runTest('webhook')">
                <div class="notification-test-card-heading"><span class="notification-test-icon webhook" aria-hidden="true">&#8644;</span>
                    <div><span class="eyebrow">02 / LOCAL DELIVERY</span><h3>Test this webhook</h3></div></div>
                <label for="notification-test-message">Test message</label>
                <input id="notification-test-message" v-model="testMessage" type="text" required :maxlength="maxTestMessageLength"
                       :disabled="busy" placeholder="A message to find in your inbox..." />
                <button type="submit" class="notification-test-button secondary" :disabled="busy || !testMessage.trim()">
                    <span>{{ activeTest === "webhook" ? "Sending..." : "Send test webhook" }}</span><span aria-hidden="true">&#8594;</span>
                </button>
                <small>Uses this application's real endpoint. Saves to Cosmos; never runs an agent.</small>
            </form>
        </div>
        <div v-if="testReceipt" class="notification-test-receipt" role="status" aria-live="polite">
            <strong>{{ testReceipt.kind === "telemetry" ? "Exception and warning trace queued." : "Test webhook accepted and saved." }}</strong>
            <span>Test ID <code>{{ testReceipt.testId }}</code> &middot; <time :datetime="testReceipt.occurredAt">{{ testReceipt.occurredAt }} UTC</time></span>
            <span v-if="testReceipt.kind === 'telemetry'">Search Application Insights for this ID after ingestion completes.</span>
        </div>
    </section>
    <section class="notification-inbox" aria-label="Webhook notifications" :aria-busy="busy">
        <form class="notification-toolbar" @submit.prevent="load(true)">
            <div class="notification-search">
                <label for="notification-search">Search messages</label>
                <div><input id="notification-search" v-model="search" type="search" :maxlength="maxSearchLength"
                            :disabled="busy" placeholder="Find a signal in the noise..." />
                    <button type="submit" :disabled="busy">Search</button></div>
            </div>
            <div><label for="notification-filter">Status</label>
                <select id="notification-filter" v-model="handled" :disabled="busy" @change="load(true)">
                    <option value="all">All notifications</option><option value="unhandled">Unhandled</option><option value="handled">Handled</option>
                </select>
            </div>
            <div><label for="notification-sort">Received date</label>
                <select id="notification-sort" v-model="sort" :disabled="busy" @change="load(true)">
                    <option value="desc">Newest first</option><option value="asc">Oldest first</option>
                </select>
            </div>
            <button type="button" class="notification-refresh" :disabled="busy" @click="load(true)">Refresh</button>
        </form>
        <div v-if="error && !completing" class="notice error" role="alert">{{ error }}</div>
        <div class="notification-caption"><span class="eyebrow">EVENT GRID / INCOMING</span>
            <span>Saved in Cosmos DB &middot; No automatic agent execution</span></div>
        <div class="table-scroll">
            <table class="notification-table">
                <caption class="sr-only">Received webhook messages and handling status</caption>
                <thead><tr><th scope="col">Received</th><th scope="col">Message / raw event</th>
                    <th scope="col">Handled / comment</th><th scope="col">Conversation</th></tr></thead>
                <tbody>
                    <tr v-for="item in items" :key="item.id" :data-notification-id="item.id" :class="{ 'notification-handled': item.isHandled }">
                        <td><span class="notification-state" :class="{ complete: item.isHandled }">{{ item.isHandled ? "Handled" : "Needs attention" }}</span>
                            <time :datetime="item.receivedDate">{{ formatDate(item.receivedDate) }}</time></td>
                        <td class="notification-message">
                            <p>{{ item.message || "No data.message was supplied." }}</p>
                            <details><summary>Raw event</summary><pre>{{ item.rawData }}</pre></details>
                        </td>
                        <td class="notification-handling">
                            <label class="notification-checkbox">
                                <input type="checkbox" :checked="item.isHandled" :disabled="busy || !canOperate"
                                       :aria-label="`Handled: ${item.message || item.id}`" @change="toggle(item, $event)" />
                                <span>{{ item.isHandled ? "Complete" : "Mark complete" }}</span>
                            </label>
                            <p v-if="item.comment" class="notification-comment">{{ item.comment }}</p>
                        </td>
                        <td class="notification-conversation">
                            <a v-if="item.canOpenSession" :href="chatUrl(item.chatSessionId)" class="notification-session">{{ item.chatSessionId }}</a>
                            <template v-else-if="item.chatSessionId"><code>{{ item.chatSessionId }}</code><small>Private to another operator</small></template>
                            <span v-else class="muted">No conversation yet</span>
                            <button v-if="canOperate && item.canStartChat" type="button" :disabled="busy || !item.message.trim()"
                                    @click="startChat(item)">{{ item.chatSessionId ? "Continue chat" : "Start chat" }} <span aria-hidden="true">&#8599;</span></button>
                        </td>
                    </tr>
                    <tr v-if="!items.length"><td colspan="4" class="empty-state">{{ busy ? "Loading notifications..." : error ? "Notifications could not be loaded." : "No notifications match. Try another search or status." }}</td></tr>
                </tbody>
            </table>
        </div>
        <div class="notification-pagination">
            <p role="status" aria-live="polite">{{ status || (busy ? "Updating inbox..." : `Page ${page + 1} / ${items.length} notifications on this page`) }}</p>
            <div><button type="button" :disabled="busy || page === 0" @click="load(false, page - 1)">Previous</button>
                <button type="button" :disabled="busy || !nextToken" @click="nextPage()">Next</button></div>
        </div>
    </section>
    <dialog ref="dialog" class="notification-dialog" aria-labelledby="notification-dialog-title"
            @cancel.prevent="!busy && closeDialog()">
        <form @submit.prevent="updateHandled(completing, true)">
            <span class="eyebrow">CLOSE THE LOOP</span><h2 id="notification-dialog-title">Mark notification complete?</h2>
            <p>The message stays in your inbox. You can mark it unhandled again at any time.</p>
            <label for="notification-comment">Completion comment <span class="muted">(optional)</span></label>
            <textarea id="notification-comment" ref="commentInput" v-model="comment" rows="4" :maxlength="maxCommentLength"
                      :disabled="busy" placeholder="What was checked or resolved?"></textarea>
            <div v-if="error" class="notice error" role="alert">{{ error }}</div>
            <div class="notification-dialog-actions"><button type="button" :disabled="busy" @click="closeDialog()">Cancel</button>
                <button type="submit" class="notification-primary" :disabled="busy">{{ busy ? "Saving..." : "OK" }}</button></div>
        </form>
    </dialog>
</template>
