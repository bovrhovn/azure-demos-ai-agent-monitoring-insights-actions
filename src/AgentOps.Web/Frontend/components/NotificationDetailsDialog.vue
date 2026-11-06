<script setup>
import { nextTick, ref, shallowRef } from "vue";

const dialog = ref(null);
const selected = shallowRef(null);
let returnFocus = null;

async function open(item, opener) {
    if (dialog.value.open) return;
    returnFocus = opener;
    selected.value = item;
    await nextTick();
    dialog.value.showModal();
}

function closed() {
    selected.value = null;
    returnFocus?.focus();
    returnFocus = null;
}

defineExpose({ open });
</script>

<template>
    <dialog ref="dialog" class="notification-details-dialog" aria-labelledby="notification-details-title"
            aria-describedby="notification-details-description" @close="closed">
        <template v-if="selected">
            <header class="notification-details-header">
                <div><p class="eyebrow">EVENT GRID / SIGNAL DETAILS</p><h2 id="notification-details-title">Notification details</h2></div>
                <button type="button" autofocus @click="dialog.close()">Close <span aria-hidden="true">&times;</span></button>
            </header>
            <div class="notification-details-body">
                <p id="notification-details-description" class="muted">Complete original message and raw event, as received. Reviewing details does not run an agent or change handling status.</p>
                <dl class="notification-details-metadata">
                    <div><dt>Notification ID</dt><dd>{{ selected.id }}</dd></div>
                    <div><dt>Received (UTC)</dt><dd><time :datetime="selected.receivedDate">{{ selected.receivedDate }}</time></dd></div>
                    <div><dt>Status</dt><dd>{{ selected.isHandled ? "Handled" : "Needs attention" }}</dd></div>
                </dl>
                <section aria-labelledby="notification-details-message-title">
                    <h3 id="notification-details-message-title">Message</h3>
                    <pre class="notification-details-message">{{ selected.message || "No data.message was supplied." }}</pre>
                </section>
                <section aria-labelledby="notification-details-raw-title">
                    <h3 id="notification-details-raw-title">Raw event</h3>
                    <pre class="notification-details-raw">{{ selected.rawData }}</pre>
                </section>
                <section v-if="selected.comment" aria-labelledby="notification-details-comment-title">
                    <h3 id="notification-details-comment-title">Completion comment</h3>
                    <p class="notification-details-comment">{{ selected.comment }}</p>
                </section>
            </div>
        </template>
    </dialog>
</template>

