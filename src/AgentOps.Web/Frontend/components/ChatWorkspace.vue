<script setup>
import { computed, nextTick, onMounted, ref } from "vue";

const props = defineProps({
    endpoint: { type: String, required: true },
    token: { type: String, required: true },
    canSend: Boolean,
    maxLength: { type: Number, required: true },
    historyLimit: { type: Number, required: true }
});
const sessions = ref([]);
const selected = ref(null);
const messages = ref([]);
const continuation = ref(null);
const hasOlder = ref(false);
const draft = ref("");
const loading = ref(false);
const sending = ref(false);
const error = ref("");
const copied = ref(null);
const timeline = ref(null);
const composer = ref(null);
const busy = computed(() => loading.value || sending.value);
const title = computed(() => sessions.value.find(session => session.id === selected.value)?.title ?? "Your next conversation");
const formatTime = value => new Date(value).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
const messagesUrl = () => `${props.endpoint}/${encodeURIComponent(selected.value)}/messages`;

async function request(url, method = "GET", body) {
    const response = await fetch(url, {
        method, credentials: "same-origin", redirect: "error",
        headers: { Accept: "application/json", "Content-Type": "application/json", RequestVerificationToken: props.token },
        body: body === undefined ? undefined : JSON.stringify(body)
    });
    if (!response.ok) {
        if (response.status === 401 || response.status === 403)
            throw new Error("Your session expired or access was denied. Reload the page to sign in.");
        if (response.status === 409) throw new Error("A reply is already in progress. Wait, then refresh this conversation.");
        if (response.status === 429) throw new Error("Too many requests. Wait a minute before trying again.");
        throw new Error(`The conversation could not be updated (HTTP ${response.status}). Refresh to check saved messages before sending again.`);
    }
    return response.json();
}

async function scrollToLatest() {
    await nextTick();
    if (timeline.value) timeline.value.scrollTop = timeline.value.scrollHeight;
}

async function readMessages(older = false) {
    const previousHeight = timeline.value?.scrollHeight ?? 0;
    const url = new URL(messagesUrl(), window.location.origin);
    if (older && messages.value.length) url.searchParams.set("before", messages.value[0].sequence);
    const page = await request(url);
    messages.value = older ? [...page.items, ...messages.value] : page.items;
    hasOlder.value = page.hasMore;
    await nextTick();
    if (older && timeline.value) timeline.value.scrollTop += timeline.value.scrollHeight - previousHeight;
    else await scrollToLatest();
}

async function readSessions(more = false) {
    const url = new URL(props.endpoint, window.location.origin);
    if (more && continuation.value) url.searchParams.set("continuationToken", continuation.value);
    const page = await request(url);
    const existing = new Set(sessions.value.map(session => session.id));
    sessions.value = more ? [...sessions.value, ...page.items.filter(session => !existing.has(session.id))] : page.items;
    continuation.value = page.continuationToken;
}

async function refresh(more = false) {
    if (busy.value) return;
    loading.value = true;
    error.value = "";
    try {
        await readSessions(more);
        if (!selected.value && sessions.value.length) selected.value = sessions.value[0].id;
        if (selected.value && !more) await readMessages();
    } catch (failure) { error.value = failure.message; }
    finally { loading.value = false; }
}

async function select(session) {
    if (busy.value || selected.value === session.id) return;
    loading.value = true;
    error.value = "";
    selected.value = session.id;
    messages.value = [];
    hasOlder.value = false;
    draft.value = "";
    try { await readMessages(); }
    catch (failure) { error.value = failure.message; }
    finally { loading.value = false; }
}

async function createSession() {
    if (busy.value) return;
    loading.value = true;
    error.value = "";
    try {
        const session = await request(props.endpoint, "POST");
        sessions.value.unshift(session);
        selected.value = session.id;
        messages.value = [];
        hasOlder.value = false;
        draft.value = "";
    } catch (failure) { error.value = failure.message; }
    finally {
        loading.value = false;
        await nextTick();
        composer.value?.focus();
    }
}

async function olderMessages() {
    if (busy.value) return;
    loading.value = true;
    error.value = "";
    try { await readMessages(true); }
    catch (failure) { error.value = failure.message; }
    finally { loading.value = false; }
}

async function send() {
    if (busy.value || !selected.value || !draft.value.trim() || !props.canSend) return;
    sending.value = true;
    error.value = "";
    try {
        await request(messagesUrl(), "POST", { content: draft.value.trim() });
        draft.value = "";
    } catch (failure) { error.value = failure.message; }
    try {
        await readMessages();
        await readSessions();
    } catch (failure) { error.value = error.value || failure.message; }
    finally {
        sending.value = false;
        await nextTick();
        composer.value?.focus();
    }
}

function keydown(event) {
    if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
        event.preventDefault();
        send();
    }
}

async function copy(message) {
    try {
        await navigator.clipboard.writeText(message.content);
        copied.value = message.id;
    } catch {
        error.value = "Clipboard access was denied. Select the message text to copy it manually.";
    }
}

onMounted(() => refresh());
</script>

<template>
    <div v-if="error" class="notice error" role="alert">
        <span>{{ error }}</span><button type="button" :disabled="busy" @click="refresh()">Refresh conversations</button>
    </div>
    <div class="chat-workspace">
        <aside class="chat-sidebar" aria-label="Chat sessions">
            <div class="chat-sidebar-heading"><span class="eyebrow">YOUR CONVERSATIONS</span>
                <button type="button" :disabled="busy || !canSend" @click="createSession()"><span aria-hidden="true">+</span> New session</button>
            </div>
            <div class="chat-session-list">
                <button v-for="session in sessions" :key="session.id" type="button" class="chat-session"
                        :class="{ selected: selected === session.id }" :aria-current="selected === session.id ? 'true' : undefined"
                        :disabled="busy" @click="select(session)">
                    <strong>{{ session.title }}</strong><time :datetime="session.updatedAt">{{ formatTime(session.updatedAt) }}</time>
                </button>
                <p v-if="!sessions.length" class="chat-sidebar-empty">{{ loading ? "Loading conversations..." : "A fresh start. Your conversations will appear here." }}</p>
                <button v-if="continuation" type="button" class="chat-load-more" :disabled="busy" @click="refresh(true)">More sessions</button>
            </div>
            <p class="chat-privacy">Private to your account.<br />Persisted in Cosmos DB.</p>
        </aside>
        <section class="chat-conversation" aria-labelledby="conversation-title" :aria-busy="busy">
            <header class="chat-conversation-heading">
                <div><span class="eyebrow">AGENT / CHAT</span><h2 id="conversation-title">{{ title }}</h2></div>
                <span class="chat-context-label">Context-aware</span>
            </header>
            <div ref="timeline" class="chat-timeline" role="region" aria-label="Message timeline" tabindex="0">
                <button v-if="hasOlder" type="button" class="chat-load-more" :disabled="busy" @click="olderMessages()">Load earlier messages</button>
                <div v-if="!messages.length" class="chat-welcome">
                    <span class="chat-orbit" aria-hidden="true">&#10035;</span>
                    <h3>Room to think out loud.</h3>
                    <p>{{ selected ? "Ask a question, explore an idea, or build on a thought." : "Create a session to begin a conversation with your agent." }}</p>
                    <p v-if="!canSend" class="muted">An operator role is required to create sessions and send messages.</p>
                </div>
                <article v-for="message in messages" :key="message.id" class="chat-message" :class="`chat-message-${message.role}`"
                         :aria-label="message.role === 'user' ? 'User prompt' : 'Agent response'">
                    <div class="chat-message-meta"><strong>{{ message.role === "user" ? "You" : "Agent" }}</strong>
                        <time :datetime="message.timestamp">{{ formatTime(message.timestamp) }}</time></div>
                    <div class="chat-bubble">{{ message.content }}</div>
                    <div v-if="message.role === 'agent'" class="chat-message-footer">
                        <span class="chat-model">Model: {{ message.model }}</span>
                        <button type="button" class="chat-copy" :aria-label="copied === message.id ? 'Message copied' : 'Copy agent message'" @click="copy(message)">
                            {{ copied === message.id ? "Copied" : "Copy" }}
                        </button>
                    </div>
                </article>
            </div>
            <p class="chat-activity" role="status" aria-live="polite">{{ sending ? "Agent is thinking. Your reply will appear here..." : loading ? "Loading conversation..." : "" }}</p>
            <form class="chat-composer" @submit.prevent="send">
                <label for="chat-prompt">Message your agent</label>
                <div class="chat-input-row">
                    <textarea id="chat-prompt" ref="composer" v-model="draft" rows="2" :maxlength="maxLength"
                              :disabled="!selected || busy || !canSend" placeholder="What would you like to explore?"
                              aria-describedby="chat-input-hint" @keydown="keydown"></textarea>
                    <button class="chat-send" type="submit" :disabled="!selected || busy || !draft.trim() || !canSend">
                        {{ sending ? "Sending..." : "Send" }} <span aria-hidden="true">&#8599;</span>
                    </button>
                </div>
                <div id="chat-input-hint" class="chat-composer-hint"><span>Enter to send &middot; Shift + Enter for a new line</span>
                    <span>{{ draft.length.toLocaleString() }} / {{ maxLength.toLocaleString() }}</span></div>
                <p class="chat-disclaimer">Replies use up to {{ historyLimit }} recent messages for context. No tools or actions are enabled. Verify important information.</p>
            </form>
        </section>
    </div>
</template>
