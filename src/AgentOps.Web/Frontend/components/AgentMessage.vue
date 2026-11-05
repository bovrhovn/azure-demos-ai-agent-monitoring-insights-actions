<script setup>
import { computed, defineComponent, h } from "vue";
import { inlineParts, presentMessage } from "../messagePresentation.mjs";

const props = defineProps({ content: { type: String, required: true } });
const blocks = computed(() => presentMessage(props.content));
const InlineText = defineComponent({
    props: { text: { type: String, required: true } },
    setup: props => () => inlineParts(props.text).map(part => h(part.tag,
        part.tag === "a" ? { href: part.href, target: "_blank", rel: "noopener noreferrer" } : null, part.text))
});
const formatTime = value => new Date(value).toLocaleString(undefined, {
    dateStyle: "medium", timeStyle: "medium"
});
const formatUtcTime = value => new Date(value).toLocaleString(undefined, {
    dateStyle: "medium", timeStyle: "medium", timeZone: "UTC"
});
</script>

<template>
    <div class="agent-content">
        <template v-for="(block, index) in blocks" :key="index">
            <section v-if="block.kind === 'queryWindows'" class="chat-query-windows" aria-label="Query windows">
                <header><span class="eyebrow">APPLICATION INSIGHTS</span><h3>Query windows <span>UTC</span></h3></header>
                <dl>
                    <div v-for="(window, windowIndex) in block.windows" :key="windowIndex">
                        <dt>{{ window.source }}</dt>
                        <dd>
                            <span class="chat-window-time"><span>From</span><time :datetime="window.start" :title="window.start">{{ formatUtcTime(window.start) }}</time></span>
                            <span class="chat-window-time"><span>To</span><time :datetime="window.end" :title="window.end">{{ formatUtcTime(window.end) }}</time></span>
                            <details><summary>Exact timestamps</summary><code>{{ window.start }} &mdash; {{ window.end }}</code></details>
                        </dd>
                    </div>
                </dl>
            </section>
            <h3 v-else-if="block.kind === 'reportHeading'" class="chat-report-heading">{{ block.title }}</h3>
            <section v-else-if="block.kind === 'report'" class="chat-insights-report" :class="`chat-report-${block.category}`"
                     :aria-label="block.title">
                <header class="chat-report-header">
                    <span class="chat-report-marker" aria-hidden="true">{{ block.category === "exceptions" ? "!" : "\u2261" }}</span>
                    <h3>{{ block.title }}</h3>
                </header>
                <template v-for="(part, partIndex) in block.parts" :key="partIndex">
                    <dl v-if="part.kind === 'fields'" class="chat-report-fields">
                        <div v-for="(field, fieldIndex) in part.fields" :key="fieldIndex" :class="{ 'chat-report-count': /^count$/i.test(field.label) }">
                            <dt>{{ field.label }}</dt>
                            <dd><code v-if="/^reference$/i.test(field.label)">{{ field.value }}</code><template v-else>{{ field.value }}</template></dd>
                        </div>
                    </dl>
                    <div v-else-if="part.kind === 'summary'" class="chat-report-summary">
                        <span class="eyebrow">{{ part.label }}</span><p><InlineText :text="part.text" /></p>
                    </div>
                    <details v-else-if="part.kind === 'details'" class="chat-report-details">
                        <summary>{{ part.label }}<span aria-hidden="true">&#8964;</span></summary>
                        <p><InlineText :text="part.text" /></p>
                    </details>
                    <aside v-else-if="part.kind === 'interpretation'" class="chat-report-interpretation" :aria-label="part.label">
                        <strong>{{ part.label }}</strong><p><InlineText :text="part.text" /></p>
                    </aside>
                    <div v-else-if="part.kind === 'entryHeading'" class="chat-report-entry-heading">
                        <h4>{{ part.label }}</h4><p v-if="part.text"><InlineText :text="part.text" /></p>
                    </div>
                    <ol v-else-if="part.kind === 'entries'" class="chat-trace-entries" aria-label="Trace entries">
                        <li v-for="(entry, entryIndex) in part.entries" :key="entryIndex">
                            <time :datetime="entry.timestamp" :title="entry.timestamp">{{ formatUtcTime(entry.timestamp) }} <span>UTC</span></time>
                            <p><InlineText :text="entry.summary" /></p>
                            <details class="chat-trace-reference">
                                <summary>Trace reference</summary>
                                <dl><div><dt>Reference</dt><dd><code>{{ entry.reference }}</code></dd></div>
                                    <div><dt>Timestamp (UTC)</dt><dd><code>{{ entry.timestamp }}</code></dd></div></dl>
                            </details>
                        </li>
                    </ol>
                    <p v-else class="chat-report-context"><InlineText :text="part.text" /></p>
                </template>
            </section>
            <section v-else-if="block.kind === 'nextSteps'" class="chat-next-steps" aria-label="Suggested next steps">
                <header class="chat-section-heading">
                    <span class="chat-section-icon" aria-hidden="true">&#8599;</span>
                    <h3><InlineText :text="block.title" /></h3>
                </header>
                <ol v-if="block.items.length" class="chat-action-list">
                    <li v-for="(item, itemIndex) in block.items" :key="itemIndex">
                        <span class="chat-action-number" aria-hidden="true">{{ String(itemIndex + 1).padStart(2, "0") }}</span>
                        <span><InlineText :text="item" /></span>
                    </li>
                </ol>
                <div v-if="block.followUp.length" class="chat-action-follow-up">
                    <p v-for="(text, textIndex) in block.followUp" :key="textIndex"><InlineText :text="text" /></p>
                </div>
            </section>
            <section v-else-if="block.kind === 'diagnostic'" class="chat-diagnostic" aria-label="Exception details">
                <header class="chat-section-heading">
                    <span class="chat-section-icon" aria-hidden="true">&#8981;</span><h3>Exception details</h3>
                </header>
                <dl class="chat-diagnostic-fields">
                    <div v-for="(field, fieldIndex) in block.fields" :key="fieldIndex" :class="{ 'chat-diagnostic-window': /^query window/i.test(field.label) }">
                        <dt>{{ field.label }}</dt>
                        <dd><span v-if="field.tone" class="chat-result-card chat-diagnostic-severity" :class="`severity-${field.tone}`">
                            <span class="chat-severity"><span class="chat-severity-dot" aria-hidden="true"></span>{{ field.value }}</span>
                        </span><code v-else>{{ field.value }}</code></dd>
                    </div>
                </dl>
            </section>
            <section v-else-if="block.kind === 'summary'" class="chat-diagnostic-summary" aria-label="Exception summary">
                <h3 class="chat-response-heading">{{ block.title }}</h3>
                <p v-for="(text, textIndex) in block.paragraphs" :key="textIndex" class="chat-response-paragraph"><InlineText :text="text" /></p>
            </section>
            <details v-else-if="block.kind === 'stack'" class="chat-stack-panel" open>
                <summary>{{ block.title }}<span aria-hidden="true">&#8964;</span></summary>
                <ol class="chat-stack-frames" aria-label="Stack frames">
                    <li v-for="(frame, frameIndex) in block.frames" :key="frameIndex">
                        <span v-if="frame.level !== null" class="chat-stack-level">Level {{ frame.level }}</span>
                        <code>{{ frame.text }}</code>
                    </li>
                </ol>
            </details>
            <aside v-else-if="block.kind === 'notes'" class="chat-diagnostic-notes" aria-label="Diagnostic notes">
                <h3>{{ block.title }}</h3>
                <p v-for="(text, textIndex) in block.paragraphs" :key="textIndex"><InlineText :text="text" /></p>
            </aside>
            <section v-else-if="block.kind === 'telemetry'" class="chat-results" aria-label="Telemetry results">
                <header class="chat-results-heading">
                    <span class="eyebrow">TELEMETRY RESULTS</span>
                    <span>{{ block.records.length }} {{ block.records.length === 1 ? "record" : "records" }}</span>
                </header>
                <ol class="chat-result-list">
                    <li v-for="(record, recordIndex) in block.records" :key="recordIndex"
                        class="chat-result-card" :class="`severity-${record.tone}`">
                        <div class="chat-result-meta">
                            <span class="chat-severity"><span aria-hidden="true" class="chat-severity-dot"></span>{{ record.label }}</span>
                            <time :datetime="record.timestamp" :title="record.timestamp">{{ formatTime(record.timestamp) }}</time>
                        </div>
                        <p class="chat-result-summary"><InlineText :text="record.summary" /></p>
                        <details class="chat-result-details">
                            <summary>Record details <span aria-hidden="true">&#8599;</span></summary>
                            <dl>
                                <div><dt>Reference</dt><dd><code>{{ record.reference }}</code></dd></div>
                                <div><dt>Timestamp</dt><dd><code>{{ record.timestamp }}</code></dd></div>
                                <div><dt>Severity</dt><dd>{{ record.severity }} / {{ record.label }}</dd></div>
                            </dl>
                        </details>
                    </li>
                </ol>
            </section>
            <h3 v-else-if="block.kind === 'heading'" class="chat-response-heading"><InlineText :text="block.text" /></h3>
            <component :is="block.ordered ? 'ol' : 'ul'" v-else-if="block.kind === 'list'" :start="block.start" class="chat-response-list">
                <li v-for="(item, itemIndex) in block.items" :key="itemIndex"><InlineText :text="item" /></li>
            </component>
            <div v-else-if="block.kind === 'code'" class="chat-code-block">
                <div class="chat-code-label">{{ block.language || "Code" }}</div>
                <pre tabindex="0" aria-label="Code example"><code>{{ block.text }}</code></pre>
            </div>
            <div v-else-if="block.kind === 'table'" class="chat-response-table" tabindex="0" role="region" aria-label="Response table">
                <table>
                    <thead><tr><th v-for="(cell, cellIndex) in block.headers" :key="cellIndex" scope="col"><InlineText :text="cell" /></th></tr></thead>
                    <tbody><tr v-for="(row, rowIndex) in block.rows" :key="rowIndex">
                        <td v-for="(cell, cellIndex) in row" :key="cellIndex"><InlineText :text="cell" /></td>
                    </tr></tbody>
                </table>
            </div>
            <p v-else class="chat-response-paragraph"><InlineText :text="block.text" /></p>
        </template>
    </div>
</template>
